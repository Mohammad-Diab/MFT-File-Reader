using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MFTFileReader;

/// <summary>
/// Method 1: asks Windows for the MFT records with FSCTL_ENUM_USN_DATA (how tools like Everything list a drive).
/// Each call fills one 64 KB buffer, so the drive is only read as far as the caller enumerates.
/// </summary>
sealed class UsnReader(char drive) : IMftSource
{
    readonly Microsoft.Win32.SafeHandles.SafeFileHandle volume = Native.OpenVolume(drive);

    public IEnumerable<MftEntry> ReadEntries()
    {
        var input = new Native.MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue };
        var buffer = new byte[64 * 1024];
        while (true)
        {
            if (!Native.DeviceIoControl(volume, Native.FSCTL_ENUM_USN_DATA, ref input, Marshal.SizeOf<Native.MFT_ENUM_DATA_V0>(),
                    buffer, buffer.Length, out int returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == Native.ERROR_HANDLE_EOF) yield break;
                throw new Win32Exception(error, "FSCTL_ENUM_USN_DATA failed");
            }
            if (returned <= 8) yield break;

            // The buffer starts with the reference number to continue from, then USN_RECORD_V2 entries.
            input.StartFileReferenceNumber = BitConverter.ToUInt64(buffer, 0);
            for (int pos = 8; pos < returned;)
            {
                int length = BitConverter.ToInt32(buffer, pos);
                if (length == 0) break;
                if (BitConverter.ToUInt16(buffer, pos + 4) == 2) // major version 2 (NTFS)
                {
                    ulong number = BitConverter.ToUInt64(buffer, pos + 8) & MftEntry.RecordMask;
                    ulong parentReference = BitConverter.ToUInt64(buffer, pos + 16);
                    uint attributes = BitConverter.ToUInt32(buffer, pos + 52);
                    int nameLength = BitConverter.ToUInt16(buffer, pos + 56);
                    int nameOffset = BitConverter.ToUInt16(buffer, pos + 58);
                    string name = Encoding.Unicode.GetString(buffer, pos + nameOffset, nameLength);
                    yield return new MftEntry(number, parentReference & MftEntry.RecordMask, parentReference, name,
                        (attributes & 0x10) != 0);
                }
                pos += length;
            }
        }
    }

    // Windows resolves the parent folder from its file reference; each folder is asked for once.
    public string GetFolderPath(MftEntry entry)
    {
        if (!folders.TryGetValue(entry.ParentReference, out string? path))
        {
            path = Native.PathFromReference(volume, entry.ParentReference) ?? $@"{drive}:\?\";
            if (!path.EndsWith(@"\")) path += @"\";
            folders[entry.ParentReference] = path;
        }
        return path;
    }

    readonly Dictionary<ulong, string> folders = new();

    public void Dispose() => volume.Dispose();
}
