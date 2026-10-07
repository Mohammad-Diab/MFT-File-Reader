using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MFTFileReader;

/// <summary>
/// Method 2: reads the MFT straight from the disk. Boot sector -> where $MFT starts -> the data runs of $MFT
/// -> every FILE record, fixed up and parsed for its $FILE_NAME. Reads 256 KB at a time, only when asked for more.
/// Happy path only: assumes $MFT's runs fit in record 0 (no $ATTRIBUTE_LIST) and records don't straddle runs.
/// </summary>
sealed class RawMftReader : IMftSource
{
    const int ChunkBytes = 256 * 1024;
    const int FixupStride = 512; // NTFS update sequence stride, whatever the physical sector size

    readonly SafeFileHandle volume;
    readonly char drive;
    readonly Dictionary<ulong, (string Name, ulong Parent)> folders = new(); // record number -> name, parent
    readonly List<(long? Lcn, long Clusters)> runs;

    public int BytesPerSector { get; }
    public int ClusterSize { get; }
    public int RecordSize { get; }
    public long RecordCount { get; }

    public RawMftReader(char drive)
    {
        this.drive = drive;
        volume = Native.OpenVolume(drive);
        byte[] boot = Read(0, 4096); // a multiple of every sector size
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            throw new InvalidDataException("The boot sector is not NTFS.");

        BytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        byte spc = boot[0x0D];
        int sectorsPerCluster = spc <= 0x80 ? spc : 1 << (256 - spc);
        ClusterSize = BytesPerSector * sectorsPerCluster;
        long mftLcn = BitConverter.ToInt64(boot, 0x30);
        sbyte perRecord = (sbyte)boot[0x40];
        RecordSize = perRecord > 0 ? perRecord * ClusterSize : 1 << -perRecord;

        // Record 0 is $MFT itself; its unnamed $DATA attribute says where the whole table lives.
        byte[] first = Read(mftLcn * ClusterSize, Math.Max(RecordSize, ClusterSize));
        if (!ApplyFixups(first, 0, RecordSize))
            throw new InvalidDataException("$MFT record 0 failed its fixup check.");
        (runs, long dataSize) = FindMftData(first);
        RecordCount = dataSize / RecordSize;
    }

    public IEnumerable<MftEntry> ReadEntries()
    {
        long index = 0;
        foreach (var (lcn, clusters) in runs)
        {
            long runBytes = clusters * ClusterSize;
            if (lcn is null) { index += runBytes / RecordSize; continue; } // sparse run, nothing on disk
            for (long done = 0; done < runBytes && index < RecordCount;)
            {
                int size = (int)Math.Min(ChunkBytes, runBytes - done);
                byte[] chunk = Read(lcn.Value * ClusterSize + done, size);
                for (int off = 0; off + RecordSize <= size && index < RecordCount; off += RecordSize, index++)
                    if (ParseRecord(chunk, off, index) is { } entry)
                    {
                        if (entry.IsDirectory) folders[entry.Number] = (entry.Name, entry.Parent);
                        yield return entry;
                    }
                done += size;
            }
        }
    }

    // Walks up from the parent to the root (record 5). Folders seen while listing are cached; a missing one costs
    // a single record read, then it is cached too.
    public string GetFolderPath(MftEntry entry)
    {
        var parts = new List<string>();
        ulong number = entry.Parent;
        for (int depth = 0; number != MftEntry.RootRecord; depth++)
        {
            if (depth > 512) return $@"{drive}:\?\"; // a loop in damaged data
            if (!folders.TryGetValue(number, out var folder))
            {
                if (ReadRecord(number) is not { } parent) return $@"{drive}:\?\";
                folder = (parent.Name, parent.Parent);
                folders[number] = folder;
            }
            parts.Add(folder.Name);
            number = folder.Parent;
        }
        parts.Reverse();
        return $@"{drive}:\" + string.Concat(parts.Select(p => p + @"\"));
    }

    // Finds where record N sits inside $MFT's runs and reads just that record.
    MftEntry? ReadRecord(ulong number)
    {
        long offset = (long)number * RecordSize;
        foreach (var (lcn, clusters) in runs)
        {
            long runBytes = clusters * ClusterSize;
            if (offset < runBytes)
                return lcn is null ? null : ParseRecord(Read(lcn.Value * ClusterSize + offset, RecordSize), 0, (long)number);
            offset -= runBytes;
        }
        return null;
    }

    MftEntry? ParseRecord(byte[] buf, int off, long index)
    {
        if (Encoding.ASCII.GetString(buf, off, 4) != "FILE" || !ApplyFixups(buf, off, RecordSize)) return null;
        ushort flags = BitConverter.ToUInt16(buf, off + 0x16);
        if ((flags & 0x01) == 0) return null;                              // record not in use
        if (BitConverter.ToUInt64(buf, off + 0x20) != 0) return null;      // extension of another record

        string? name = null; ulong parentReference = 0; int bestRank = -1;
        int end = off + RecordSize;
        for (int pos = off + BitConverter.ToUInt16(buf, off + 0x14); pos + 16 <= end;)
        {
            uint type = BitConverter.ToUInt32(buf, pos);
            int length = BitConverter.ToInt32(buf, pos + 4);
            if (type == 0xFFFFFFFF || length <= 0 || pos + length > end) break;
            if (type == 0x30 && buf[pos + 8] == 0) // resident $FILE_NAME
            {
                int c = pos + BitConverter.ToUInt16(buf, pos + 0x14);
                if (c + 0x42 > end) break; // damaged record
                int nameLength = buf[c + 0x40];
                byte ns = buf[c + 0x41];
                int rank = ns == 2 ? 0 : ns == 0 ? 1 : 2; // prefer Win32 names over POSIX, and both over 8.3 DOS
                if (rank > bestRank && c + 0x42 + nameLength * 2 <= end)
                {
                    bestRank = rank;
                    parentReference = BitConverter.ToUInt64(buf, c);
                    name = Encoding.Unicode.GetString(buf, c + 0x42, nameLength * 2);
                }
            }
            pos += length;
        }
        return name is null ? null
            : new MftEntry((ulong)index, parentReference & MftEntry.RecordMask, parentReference, name, (flags & 0x02) != 0);
    }

    (List<(long?, long)> Runs, long DataSize) FindMftData(byte[] buf)
    {
        for (int pos = BitConverter.ToUInt16(buf, 0x14); pos + 16 <= RecordSize;)
        {
            uint type = BitConverter.ToUInt32(buf, pos);
            int length = BitConverter.ToInt32(buf, pos + 4);
            if (type == 0xFFFFFFFF || length <= 0) break;
            if (type == 0x20)
                Console.WriteLine("Note: $MFT has an attribute list; this happy-path reader may miss part of the table.");
            if (type == 0x80 && buf[pos + 8] == 1 && buf[pos + 9] == 0) // non-resident, unnamed $DATA
                return (ParseRuns(buf, pos + BitConverter.ToUInt16(buf, pos + 0x20)), BitConverter.ToInt64(buf, pos + 0x30));
            pos += length;
        }
        throw new InvalidDataException("$MFT has no $DATA attribute in record 0.");
    }

    // Data runs: a header byte (low nibble = size of the length, high nibble = size of the offset),
    // then the cluster count and a signed LCN offset relative to the previous run. 0 ends the list.
    static List<(long?, long)> ParseRuns(byte[] buf, int pos)
    {
        var list = new List<(long?, long)>();
        long lcn = 0;
        while (buf[pos] != 0)
        {
            int lengthSize = buf[pos] & 0x0F, offsetSize = buf[pos] >> 4;
            pos++;
            long clusters = ReadLittleEndian(buf, pos, lengthSize, signed: false);
            pos += lengthSize;
            if (offsetSize == 0) { list.Add((null, clusters)); continue; }
            lcn += ReadLittleEndian(buf, pos, offsetSize, signed: true);
            pos += offsetSize;
            list.Add((lcn, clusters));
        }
        return list;
    }

    static long ReadLittleEndian(byte[] buf, int pos, int size, bool signed)
    {
        long value = 0;
        for (int i = size - 1; i >= 0; i--) value = (value << 8) | buf[pos + i];
        if (signed && size < 8 && (buf[pos + size - 1] & 0x80) != 0) value -= 1L << (size * 8);
        return value;
    }

    // Each 512-byte stride of a record ends with the update sequence number; the real bytes are kept in the array.
    static bool ApplyFixups(byte[] buf, int off, int recordSize)
    {
        int usaOffset = BitConverter.ToUInt16(buf, off + 4), usaCount = BitConverter.ToUInt16(buf, off + 6);
        ushort usn = BitConverter.ToUInt16(buf, off + usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int at = off + i * FixupStride - 2;
            if (at + 2 > off + recordSize) break;
            if (BitConverter.ToUInt16(buf, at) != usn) return false; // torn or damaged record
            buf[at] = buf[off + usaOffset + 2 * i];
            buf[at + 1] = buf[off + usaOffset + 2 * i + 1];
        }
        return true;
    }

    byte[] Read(long offset, int count) => Native.ReadAt(volume, offset, count);

    public void Dispose() => volume.Dispose();
}
