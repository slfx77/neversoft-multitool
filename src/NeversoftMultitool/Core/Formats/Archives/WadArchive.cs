using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
/// Extracts files from Neversoft WAD archives using companion HED files.
/// Credit to JayRedFox: https://github.com/JayFoxRox/thps2-tools/blob/master/extract-hed-wad.py
/// </summary>
public static class WadArchive
{
    /// <summary>
    /// Gets the companion HED file path for a WAD file.
    /// </summary>
    public static string GetHedPath(string wadPath)
    {
        var directory = Path.GetDirectoryName(wadPath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(wadPath);
        return Path.Combine(directory, nameWithoutExt + ".HED");
    }

    /// <summary>
    /// Reads the file list from the HED companion file.
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string wadPath)
    {
        var hedPath = GetHedPath(wadPath);
        if (!File.Exists(hedPath))
            throw new FileNotFoundException($"Companion HED file not found: {hedPath}");

        var entries = new List<ArchiveEntry>();

        using var stream = File.OpenRead(hedPath);
        using var reader = new BinaryReader(stream);

        var fileSize = stream.Length;

        // Read entries until near end of file (leave 7 bytes to prevent overflow)
        while (stream.Position < fileSize - 7)
        {
            var name = ReadNullTerminatedString(reader);
            AlignStream(stream, 4);
            var offset = reader.ReadUInt32();
            var size = reader.ReadUInt32();

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Size = size,
                Offset = offset
            });
        }

        // Verify terminator
        var terminator = reader.ReadByte();
        if (terminator != 0xFF)
            throw new InvalidDataException("Invalid HED file: missing 0xFF terminator");

        return entries;
    }

    /// <summary>
    /// Extracts all files from a WAD archive.
    /// </summary>
    public static void ExtractFiles(string wadPath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        var entries = GetFileList(wadPath);
        var archiveName = Path.GetFileNameWithoutExtension(wadPath);

        using var wadStream = File.OpenRead(wadPath);

        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var entry = entries[i];
            wadStream.Seek(entry.Offset, SeekOrigin.Begin);

            var exportPath = Path.Combine(outputDir, archiveName, entry.Name);
            var exportDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(exportDir))
                Directory.CreateDirectory(exportDir);

            var data = new byte[entry.Size];
            wadStream.ReadExactly(data);

            File.WriteAllBytes(exportPath, data);

            onFileExtracted?.Invoke(i + 1, entries.Count);
        }
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static void AlignStream(Stream stream, int alignment)
    {
        var remainder = stream.Position % alignment;
        if (remainder != 0)
            stream.Position += alignment - remainder;
    }
}
