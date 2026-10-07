namespace MFTFileReader;

/// <summary>
/// One file or folder as found in the MFT. Number and Parent are MFT record numbers;
/// ParentReference is the full file reference (record number + sequence number) Windows needs to open the parent.
/// </summary>
record MftEntry(ulong Number, ulong Parent, ulong ParentReference, string Name, bool IsDirectory)
{
    /// <summary>File reference numbers keep the record number in the low 48 bits (the high 16 are a sequence number).</summary>
    public const ulong RecordMask = 0x0000FFFFFFFFFFFF;

    /// <summary>Record 5 is always the root folder of the drive.</summary>
    public const ulong RootRecord = 5;
}

/// <summary>A way of reading a drive's MFT: lists the entries lazily and can tell the folder an entry lives in.</summary>
interface IMftSource : IDisposable
{
    IEnumerable<MftEntry> ReadEntries();

    /// <summary>The folder path (ending with \) of the entry's parent, looked up only when needed and then cached.</summary>
    string GetFolderPath(MftEntry entry);
}
