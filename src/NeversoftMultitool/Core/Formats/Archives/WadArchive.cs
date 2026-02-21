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
    /// Supports both plaintext HED (null-terminated names) and hashed HED
    /// (12-byte entries: uint32 hash, uint32 offset, uint32 size).
    /// </summary>
    public static List<ArchiveEntry> GetFileList(string wadPath)
    {
        var hedPath = GetHedPath(wadPath);
        if (!File.Exists(hedPath))
            throw new FileNotFoundException($"Companion HED file not found: {hedPath}");

        using var stream = File.OpenRead(hedPath);
        using var reader = new BinaryReader(stream);

        // Detect format by probing the first bytes.
        // Plaintext HED: printable ASCII bytes terminated by null (a filename).
        // Hashed HED: uint32 hash where non-printable bytes appear before any null.
        var probe = reader.ReadBytes((int)Math.Min(12, stream.Length));
        stream.Position = 0;

        var isPlaintext = false;
        for (var i = 0; i < probe.Length; i++)
        {
            if (probe[i] == 0) { isPlaintext = i >= 2; break; }
            if (probe[i] is < 0x20 or > 0x7E) break;
        }

        return isPlaintext
            ? ReadPlaintextEntries(reader, stream.Length)
            : ReadHashedEntries(reader, stream.Length);
    }

    private static List<ArchiveEntry> ReadPlaintextEntries(BinaryReader reader, long fileSize)
    {
        var entries = new List<ArchiveEntry>();

        while (reader.BaseStream.Position < fileSize - 7)
        {
            var name = ReadNullTerminatedString(reader);
            AlignStream(reader.BaseStream, 4);
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

    private static List<ArchiveEntry> ReadHashedEntries(BinaryReader reader, long fileSize)
    {
        var entries = new List<ArchiveEntry>();

        while (reader.BaseStream.Position + 12 <= fileSize)
        {
            var hash = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var size = reader.ReadUInt32();

            // Sentinel: all zeros marks end of entries
            if (hash == 0 && offset == 0 && size == 0)
                break;

            // Try to resolve the hash to a filename using Crc32Neversoft dictionary
            var resolvedName = HedDictionary.TryResolve(hash);
            var name = resolvedName ?? $"{hash:X8}.dat";

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Crc = hash,
                Size = size,
                Offset = offset
            });
        }

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
