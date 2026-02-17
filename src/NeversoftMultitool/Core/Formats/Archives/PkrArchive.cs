using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
/// Extracts files from Neversoft PKR3 archives.
/// </summary>
public static class PkrArchive
{
    private const uint FileCompressed = 0x00000002;

    /// <summary>
    /// Reads the file list from a PKR archive.
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string pkrPath)
    {
        using var stream = File.OpenRead(pkrPath);
        using var reader = new BinaryReader(stream);

        var (dirs, _) = SetupDirectories(reader);
        return ReadAllFileEntries(reader, dirs);
    }

    /// <summary>
    /// Extracts all files from a PKR archive.
    /// </summary>
    public static void ExtractFiles(string pkrPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        using var stream = File.OpenRead(pkrPath);
        using var reader = new BinaryReader(stream);

        var (dirs, _) = SetupDirectories(reader);
        var allEntries = ReadAllFileEntries(reader, dirs);

        var totalFiles = allEntries.Count;
        var filesProcessed = 0;

        // Reset to after directory headers to read file entries again for extraction
        stream.Seek(0, SeekOrigin.Begin);
        var (extractDirs, _) = SetupDirectories(reader);

        foreach (var dir in extractDirs)
        {
            var extractedPath = Path.Combine(outputDir, dir.Name);
            Directory.CreateDirectory(extractedPath);

            for (var i = 0; i < dir.NumFiles; i++)
            {
                token.ThrowIfCancellationRequested();

                var fileEntry = ReadFileEntry(reader);
                var originalPos = stream.Position;

                stream.Seek(fileEntry.Offset, SeekOrigin.Begin);

                var fileSize = fileEntry.IsCompressed ? fileEntry.CompressedSize : fileEntry.Size;
                var data = new byte[fileSize];
                stream.ReadExactly(data);

                byte[] outputData;
                if (fileEntry.IsCompressed)
                {
                    outputData = DecompressData(data, (int)fileEntry.Size);
                }
                else
                {
                    outputData = data;
                }

                // Verify CRC
                var crc = CalculateCrc32(outputData);
                if (crc != fileEntry.Crc)
                    throw new InvalidDataException($"CRC mismatch for {fileEntry.Name}");

                var outputPath = Path.Combine(extractedPath, fileEntry.Name);
                File.WriteAllBytes(outputPath, outputData);

                stream.Seek(originalPos, SeekOrigin.Begin);

                filesProcessed++;
                onFileExtracted?.Invoke(filesProcessed, totalFiles);
            }
        }
    }

    private static (List<PkrDir> dirs, PkrDirHeader header) SetupDirectories(BinaryReader reader)
    {
        // Read PKR3 file header
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4)).TrimEnd('\0');
        if (magic != "PKR3")
            throw new InvalidDataException("Invalid PKR3 header");

        var dirOffset = reader.ReadUInt32();

        // Seek to directory header
        reader.BaseStream.Seek(dirOffset, SeekOrigin.Begin);

        var unk = reader.ReadUInt32();
        var numDirs = reader.ReadUInt32();
        var numFiles = reader.ReadUInt32();
        var header = new PkrDirHeader(unk, (int)numDirs, (int)numFiles);

        // Read directory entries
        var dirs = new List<PkrDir>((int)numDirs);
        for (var i = 0; i < numDirs; i++)
        {
            var nameBytes = reader.ReadBytes(32);
            var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            var dirUnk = reader.ReadUInt32();
            var dirNumFiles = reader.ReadUInt32();
            dirs.Add(new PkrDir(name, dirUnk, (int)dirNumFiles));
        }

        return (dirs, header);
    }

    private static List<ArchiveEntry> ReadAllFileEntries(BinaryReader reader, List<PkrDir> dirs)
    {
        var entries = new List<ArchiveEntry>();

        foreach (var dir in dirs)
        {
            for (var i = 0; i < dir.NumFiles; i++)
            {
                var entry = ReadFileEntry(reader);
                entry.Directory = dir.Name;
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static ArchiveEntry ReadFileEntry(BinaryReader reader)
    {
        var nameBytes = reader.ReadBytes(32);
        var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
        var crc = reader.ReadUInt32();
        var compressed = reader.ReadUInt32();
        var fileOffset = reader.ReadUInt32();
        var uncompressedSize = reader.ReadUInt32();
        var compressedSize = reader.ReadUInt32();

        return new ArchiveEntry
        {
            Name = name,
            Crc = crc,
            IsCompressed = compressed == FileCompressed,
            Offset = fileOffset,
            Size = uncompressedSize,
            CompressedSize = compressedSize
        };
    }

    private static byte[] DecompressData(byte[] compressedData, int uncompressedSize)
    {
        using var input = new MemoryStream(compressedData);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[uncompressedSize];
        zlib.ReadExactly(output);

        return output;
    }

    private static uint CalculateCrc32(byte[] data)
    {
        return Crc32.HashToUInt32(data);
    }

    private sealed record PkrDirHeader(uint Unk, int NumDirs, int NumFiles);
    private sealed record PkrDir(string Name, uint Unk, int NumFiles);
}
