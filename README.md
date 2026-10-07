# MFT File Reader

A small Windows console tool that lists every file and folder on an NTFS drive straight from its **Master File Table (MFT)**, with full paths, one page at a time.

It reads the MFT in two ways, so you can compare them:

1. **USN enumeration:** asks Windows for the MFT records with `FSCTL_ENUM_USN_DATA` (the way tools like Everything list a drive). Fast and simple.
2. **Raw MFT read:** opens the volume and parses NTFS itself: the boot sector, the data runs of `$MFT`, every `FILE` record with its fixups, and the `$FILE_NAME` attribute.

## Safe by design

- **Read-only:** the drive is opened for reading only. Nothing on it is changed.
- **Light on the disk:** it reads only what the next page needs (64 KB per call for method 1, 256 KB for method 2), so it doesn't load the whole table or keep the disk busy.
- **Checks first:** it makes sure the drive exists, is ready and is NTFS before reading anything.

## Usage

Run it from a terminal opened **as Administrator** (reading the MFT needs it):

```
MFTFileReader.exe
```

1. Enter the drive letter (Enter = `C`).
2. Choose the method: `1` (USN) or `2` (raw MFT).
3. Press **Enter** for the next page, **Q** to quit.

Each line shows the MFT record number, whether it is a file or a folder, and its full path:

```
      Record  Type  Path
           0  file  C:\$MFT
           1  file  C:\$MFTMirr
           2  file  C:\$LogFile
```

Paths are built by walking up the parent folders. Each folder is looked up once and then cached.

## Build

Open `MFTFileReader.sln` in Visual Studio, or run `dotnet build -c Release`. It targets .NET Framework 4.8, which is part of Windows 10 and 11.

## Limitations (raw method)

The raw reader covers the normal case. It assumes the list of `$MFT` fragments fits in its first record (no `$ATTRIBUTE_LIST`), which holds on almost every drive. If it doesn't, it prints a note and may miss part of the table.

## License

[MIT](LICENSE)
