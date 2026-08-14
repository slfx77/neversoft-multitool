using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Extracts files from Neversoft PKR3 archives.
/// </summary>
public static class PkrArchive
{
    private const uint FileCompressed = 0x00000002;
    private const int FileHeaderSize = 8;
    private const int DirectoryHeaderSize = 12;
    private const int DirectoryEntrySize = 40;
    private const int FileEntrySize = 52;

    /// <summary>
    ///     Reads the file list from a PKR archive.
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string pkrPath)
    {
        using var stream = File.OpenRead(pkrPath);
        using var reader = new BinaryReader(stream);

        var (dirs, _) = SetupDirectories(reader);
        return ReadAllFileEntries(reader, dirs);
    }

    /// <summary>
    ///     In-memory variant for PKR archives nested inside another archive.
    /// </summary>
    public static List<ArchiveEntry> GetFileList(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream);

        var (dirs, _) = SetupDirectories(reader);
        return ReadAllFileEntries(reader, dirs);
    }

    /// <summary>
    ///     Extracts all files from a PKR archive.
    /// </summary>
    public static void ExtractFiles(string pkrPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(pkrPath);
        using var reader = new BinaryReader(stream);

        var (dirs, _) = SetupDirectories(reader);
        var allEntries = ReadAllFileEntries(reader, dirs);
        var outputRoot = Path.GetFullPath(outputDir);
        var directoryPaths = new string[dirs.Count];
        for (var i = 0; i < dirs.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            directoryPaths[i] = string.IsNullOrEmpty(dirs[i].Name)
                ? outputRoot
                : ArchiveExtractionPath.GetContainedPath(
                    outputRoot, dirs[i].Name, "PKR directory");
        }

        var exportPaths = new string[allEntries.Count];
        for (var i = 0; i < allEntries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            exportPaths[i] = ArchiveExtractionPath.GetContainedPath(
                outputRoot, allEntries[i].FullName, "PKR entry");
        }

        var totalFiles = allEntries.Count;
        var filesProcessed = 0;

        // Reset to after directory headers to read file entries again for extraction
        stream.Seek(0, SeekOrigin.Begin);
        var (extractDirs, _) = SetupDirectories(reader);

        for (var directoryIndex = 0; directoryIndex < extractDirs.Count; directoryIndex++)
        {
            token.ThrowIfCancellationRequested();
            var dir = extractDirs[directoryIndex];
            var extractedPath = directoryPaths[directoryIndex];
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

                var outputPath = exportPaths[filesProcessed];
                File.WriteAllBytes(outputPath, outputData);

                stream.Seek(originalPos, SeekOrigin.Begin);

                filesProcessed++;
                onFileExtracted?.Invoke(filesProcessed, totalFiles);
            }
        }
    }

    private static (List<PkrDir> dirs, PkrDirHeader header) SetupDirectories(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        RequireRange(stream, 0, FileHeaderSize, "PKR3 file header");

        // Read PKR3 file header
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4)).TrimEnd('\0');
        if (magic != "PKR3")
            throw new InvalidDataException("Invalid PKR3 header");

        var dirOffset = reader.ReadUInt32();

        // Seek to directory header
        if (dirOffset < FileHeaderSize)
            throw new InvalidDataException(
                $"PKR3 directory header offset {dirOffset} overlaps the {FileHeaderSize}-byte file header");
        RequireRange(stream, dirOffset, DirectoryHeaderSize, "PKR3 directory header");
        stream.Seek(dirOffset, SeekOrigin.Begin);

        var unk = reader.ReadUInt32();
        var numDirs = reader.ReadUInt32();
        var numFiles = reader.ReadUInt32();
        var header = new PkrDirHeader(unk, numDirs, numFiles);

        var directoryTableOffset = stream.Position;
        var directoryTableSize = (long)numDirs * DirectoryEntrySize;
        RequireRange(stream, directoryTableOffset, directoryTableSize, "PKR3 directory table");
        if (numDirs > int.MaxValue)
            throw new InvalidDataException($"PKR3 directory count {numDirs} exceeds the supported range");

        // Read directory entries
        var dirs = new List<PkrDir>((int)numDirs);
        for (var i = 0; i < (int)numDirs; i++)
        {
            var nameBytes = reader.ReadBytes(32);
            // PKR directory records are inconsistent about carrying a trailing
            // slash (Spider-Man PC's data.pkr stores "data/"). ArchiveEntry
            // supplies the separator in FullName, so retain a canonical
            // relative directory here rather than producing "data//file" in
            // scanner rows, tooltips, and path indexes.
            var name = Encoding.ASCII.GetString(nameBytes)
                .TrimEnd('\0')
                .Replace('\\', '/')
                .Trim('/');
            var dirUnk = reader.ReadUInt32();
            var dirNumFiles = reader.ReadUInt32();
            if (dirNumFiles > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"PKR3 directory {i} file count {dirNumFiles} exceeds the supported range");
            }

            dirs.Add(new PkrDir(name, dirUnk, (int)dirNumFiles));
        }

        return (dirs, header);
    }

    private static List<ArchiveEntry> ReadAllFileEntries(BinaryReader reader, List<PkrDir> dirs)
    {
        long entryCount = 0;
        foreach (var dir in dirs)
            entryCount += dir.NumFiles;

        if (entryCount > int.MaxValue)
            throw new InvalidDataException($"PKR3 file count {entryCount} exceeds the supported range");

        var stream = reader.BaseStream;
        var remaining = stream.Length - stream.Position;
        if (entryCount > remaining / FileEntrySize)
        {
            throw new InvalidDataException(
                $"PKR3 file table for {entryCount} entries runs past end of file");
        }

        var entries = new List<ArchiveEntry>((int)entryCount);

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
        var stream = reader.BaseStream;
        RequireRange(stream, stream.Position, FileEntrySize, "PKR3 file entry");

        var nameBytes = reader.ReadBytes(32);
        var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
        var crc = reader.ReadUInt32();
        var compressed = reader.ReadUInt32();
        var fileOffset = reader.ReadUInt32();
        var uncompressedSize = reader.ReadUInt32();
        var compressedSize = reader.ReadUInt32();
        var storedSize = compressed == FileCompressed ? compressedSize : uncompressedSize;
        RequireRange(stream, fileOffset, storedSize, $"PKR3 entry '{name}' payload");

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

    private static void RequireRange(Stream stream, long offset, long size, string context)
    {
        if (offset < 0 || size < 0 || offset > stream.Length || size > stream.Length - offset)
        {
            throw new InvalidDataException(
                $"{context} range [{offset}, {offset + size}) is outside file length {stream.Length}");
        }
    }

    private sealed record PkrDirHeader(uint Unk, uint NumDirs, uint NumFiles);

    private sealed record PkrDir(string Name, uint Unk, int NumFiles);
}
