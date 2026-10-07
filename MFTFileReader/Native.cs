using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MFTFileReader;

/// <summary>Win32 calls used to open a volume read-only and enumerate its MFT through the USN API.</summary>
static class Native
{
    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
    const uint OPEN_EXISTING = 3;
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const int ERROR_HANDLE_EOF = 38;

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_ENUM_DATA_V0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref MFT_ENUM_DATA_V0 input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);

    const uint FILE_READ_ATTRIBUTES = 0x80;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // needed to open a folder

    // FILE_ID_DESCRIPTOR: size, type (0 = 64-bit file id), then a 16-byte union of which only the 64-bit id is used.
    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ID_DESCRIPTOR
    {
        public int Size;
        public int Type;
        public ulong FileId;
        public ulong Unused;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id, uint access,
        uint share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] path, uint size, uint flags);

    /// <summary>Full path of the file or folder with this file reference, or null if Windows won't open it.</summary>
    public static string? PathFromReference(SafeFileHandle volume, ulong reference)
    {
        var id = new FILE_ID_DESCRIPTOR { Size = Marshal.SizeOf<FILE_ID_DESCRIPTOR>(), Type = 0, FileId = reference };
        using var handle = OpenFileById(volume, ref id, FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | 0x4 /* delete */, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
        if (handle.IsInvalid) return null;
        var buffer = new char[32768];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        string path = new(buffer, 0, (int)length);
        return path.StartsWith(@"\\?\") ? path.Substring(4) : path;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int count, out int read, IntPtr overlapped);

    /// <summary>Reads count bytes at offset. On a volume both must be multiples of the sector size.</summary>
    public static byte[] ReadAt(SafeFileHandle volume, long offset, int count)
    {
        if (!SetFilePointerEx(volume, offset, out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot seek to {offset}");
        var buffer = new byte[count];
        if (!ReadFile(volume, buffer, count, out int read, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot read {count} bytes at {offset}");
        if (read != count) throw new EndOfStreamException($"Unexpected end of volume at {offset + read}.");
        return buffer;
    }

    /// <summary>Opens \\.\X: for reading only. Other programs keep full access to the drive.</summary>
    public static SafeFileHandle OpenVolume(char drive)
    {
        var handle = CreateFile($@"\\.\{drive}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open drive {drive}:");
        return handle;
    }
}
