// See https://aka.ms/new-console-template for more information
using System;
using System.IO;
using MFTReader;

class Program
{
    static void Main()
    {
        try
        {
            string ntfsVolumePath = @"C:\"; // Replace with the actual path to your NTFS volume or disk

            using (var mftReader = new MFTReader(ntfsVolumePath))
            {
                var mftEntries = mftReader.ReadMFT();

                foreach (var entry in mftEntries)
                {
                    Console.WriteLine($"MFT Entry {entry.EntryNumber}: {entry.FileName}");
                    // You can access more information about the entry using other properties of the entry object
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
        }
    }
}
