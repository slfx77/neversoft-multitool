using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Extracts DDS textures from Neversoft DDX texture archives (original Xbox).
///     DDX files contain a table of contents followed by concatenated standard DDS files.
/// </summary>
public static class DdxArchive
{
    private const int HeaderSize = 16;
    private const int NameFieldSize = 256;
    private const int TocEntrySize = 8 + NameFieldSize;

    /// <summary>
    ///     Reads the file list from a DDX archive.
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string ddxPath)
    {
        using var stream = File.OpenRead(ddxPath);
        return ReadFileList(stream);
    }

    /// <summary>
    ///     Reads the file list from an in-memory DDX archive buffer.
    /// </summary>
    public static List<ArchiveEntry> GetFileList(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        return ReadFileList(stream);
    }

    private static List<ArchiveEntry> ReadFileList(Stream stream)
    {
        if (stream.Length < HeaderSize)
            throw new InvalidDataException(
                $"DDX header is truncated ({stream.Length} bytes, expected at least {HeaderSize})");

        using var reader = new BinaryReader(stream, Encoding.ASCII, true);

        // Header: 4 reserved + 4 fileSize + 4 dataOffset + 4 entryCount
        reader.ReadUInt32(); // reserved (always 0)
        reader.ReadUInt32(); // file size
        var dataOffset = reader.ReadUInt32();
        var entryCount = reader.ReadUInt32();

        var tableEnd = HeaderSize + (long)entryCount * TocEntrySize;
        if (tableEnd > stream.Length)
            throw new InvalidDataException(
                $"DDX table for {entryCount} entries ends at {tableEnd}, past file length {stream.Length}");
        if (entryCount > int.MaxValue)
            throw new InvalidDataException($"DDX entry count {entryCount} exceeds the supported range");
        if (dataOffset < tableEnd || dataOffset > stream.Length)
            throw new InvalidDataException(
                $"DDX data offset {dataOffset} is outside the bounded data region {tableEnd}..{stream.Length}");

        var entries = new List<ArchiveEntry>((int)entryCount);

        for (var i = 0; i < entryCount; i++)
        {
            var relativeOffset = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var nameBytes = reader.ReadBytes(NameFieldSize);
            var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

            var offset = (long)dataOffset + relativeOffset;
            if (offset < dataOffset || offset > stream.Length || size > stream.Length - offset)
                throw new InvalidDataException(
                    $"DDX entry {i} payload [{offset}, {offset + size}) is outside file length {stream.Length}");

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Size = size,
                Offset = offset
            });
        }

        return entries;
    }

    /// <summary>
    ///     Reads all entries from a DDX archive into memory.
    ///     Returns a dictionary keyed by filename stem (no extension, case-insensitive).
    /// </summary>
    public static Dictionary<string, byte[]> ReadAllEntries(string ddxPath)
    {
        using var stream = File.OpenRead(ddxPath);
        return ReadAllEntriesFromStream(stream);
    }

    /// <summary>
    ///     In-memory variant of <see cref="ReadAllEntries(string)" />.
    /// </summary>
    public static Dictionary<string, byte[]> ReadAllEntries(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        return ReadAllEntriesFromStream(stream);
    }

    private static Dictionary<string, byte[]> ReadAllEntriesFromStream(Stream stream)
    {
        var entries = ReadFileList(stream);
        var result = new Dictionary<string, byte[]>(entries.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            stream.Seek(entry.Offset, SeekOrigin.Begin);
            var data = new byte[entry.Size];
            stream.ReadExactly(data);
            var name = Path.GetFileNameWithoutExtension(entry.Name);
            result.TryAdd(name, data);
        }

        return result;
    }

    /// <summary>
    ///     Extracts all DDS textures from a DDX archive.
    /// </summary>
    public static void ExtractFiles(string ddxPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var entries = GetFileList(ddxPath);
        var archiveName = Path.GetFileNameWithoutExtension(ddxPath);
        var outputRoot = Path.GetFullPath(outputDir);
        var extractionRoot = ArchiveExtractionPath.GetContainedPath(
            outputRoot, archiveName, "DDX extraction directory");
        var exportPaths = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            exportPaths[i] = ArchiveExtractionPath.GetContainedPath(
                extractionRoot, entries[i].Name, "DDX entry");
        }

        using var stream = File.OpenRead(ddxPath);

        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var entry = entries[i];
            stream.Seek(entry.Offset, SeekOrigin.Begin);

            var exportPath = exportPaths[i];
            var exportDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(exportDir))
                Directory.CreateDirectory(exportDir);

            var data = new byte[entry.Size];
            stream.ReadExactly(data);

            File.WriteAllBytes(exportPath, data);

            onFileExtracted?.Invoke(i + 1, entries.Count);
        }
    }
}
