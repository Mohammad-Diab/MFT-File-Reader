using System.Security.Principal;
using System.Text;
using MFTFileReader;

// Lists the files of an NTFS drive from its MFT, one page at a time. Read-only: it never writes to the drive.
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("MFT File Reader (read-only)\n");

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.WriteLine("Reading the MFT needs administrator rights. Open the terminal as Administrator and run it again.");
    return 1;
}

char drive = AskDrive();
if (drive == '\0') return 1;

Console.WriteLine("\nMethod:");
Console.WriteLine("  1) USN enumeration - Windows lists the MFT records (fast, simple)");
Console.WriteLine("  2) Raw MFT read    - parses the MFT straight from the disk");
string method = Ask("Choose 1 or 2 [1]: ", "1");
if (method is not ("1" or "2"))
{
    Console.WriteLine("Unknown method.");
    return 1;
}
int pageSize = Math.Max(10, Console.WindowHeight - 6);

try
{
    if (method == "1")
    {
        using var reader = new UsnReader(drive);
        Show(reader, pageSize);
    }
    else
    {
        using var reader = new RawMftReader(drive);
        Console.WriteLine($"\nMFT: {reader.RecordCount:N0} records of {reader.RecordSize} bytes, cluster {reader.ClusterSize} bytes, sector {reader.BytesPerSector} bytes");
        Show(reader, pageSize);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\nError: {ex.Message}");
    return 1;
}
return 0;

static char AskDrive()
{
    string input = Ask("Drive letter [C]: ", "C").TrimEnd(':', '\\').ToUpperInvariant();
    if (input.Length != 1 || input[0] is < 'A' or > 'Z')
    {
        Console.WriteLine("That is not a drive letter.");
        return '\0';
    }
    var info = new DriveInfo($"{input}:\\");
    if (!info.IsReady)
    {
        Console.WriteLine($"Drive {input}: does not exist or is not ready.");
        return '\0';
    }
    if (info.DriveFormat != "NTFS")
    {
        Console.WriteLine($"Drive {input}: is {info.DriveFormat}, not NTFS, so it has no MFT.");
        return '\0';
    }
    Console.WriteLine($"Drive {input}: is NTFS ({info.VolumeLabel}, {info.TotalSize / (1024 * 1024 * 1024)} GB). OK.");
    return input[0];
}

static string Ask(string prompt, string fallback)
{
    Console.Write(prompt);
    string? line = Console.ReadLine()?.Trim();
    return line is { Length: > 0 } ? line : fallback;
}

// Pulls only one page of entries from the reader at a time, so the disk is read in small steps.
static void Show(IMftSource source, int pageSize)
{
    using var e = source.ReadEntries().GetEnumerator();
    int page = 0;
    long shown = 0;
    while (true)
    {
        Console.WriteLine($"\n{"Record",12}  {"Type",-4}  Path");
        int n = 0;
        while (n < pageSize && e.MoveNext())
        {
            var x = e.Current;
            // Record 5 is the root itself (named "."); its parent is itself, so its folder path is the drive root.
            string path = x.Number == MftEntry.RootRecord ? source.GetFolderPath(x) : source.GetFolderPath(x) + x.Name;
            Console.WriteLine($"{x.Number,12}  {(x.IsDirectory ? "dir" : "file"),-4}  {path}");
            n++;
        }
        shown += n;
        page++;
        if (n < pageSize)
        {
            Console.WriteLine($"\nDone. {shown:N0} entries.");
            return;
        }
        Console.Write($"-- page {page}, {shown:N0} entries so far -- Enter: next page, Q: quit ");
        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();
        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape) return;
    }
}
