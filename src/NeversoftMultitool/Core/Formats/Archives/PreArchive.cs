using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Extracts files from Neversoft PRE archives.
///     PRE is a simple flat archive format with no compression, used in THPS1 (PS1),
///     THPS2 (PS1 and Dreamcast). Contains BMP images, fonts, PSX models, and scripts.
/// </summary>
public static class PreArchive
{
    private const int HeaderSize = sizeof(uint);
    private const int MinimumEntrySize = 8;

    public static List<ArchiveEntry> GetFileList(string prePath)
    {
        using var stream = File.OpenRead(prePath);
        return GetFileList(stream);
    }

    /// <summary>
    ///     In-memory variant for PRE archives nested inside another archive.
    /// </summary>
    public static List<ArchiveEntry> GetFileList(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        return GetFileList(stream);
    }

    private static List<ArchiveEntry> GetFileList(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);

        if (stream.Length < HeaderSize)
            throw new InvalidDataException(
                $"Plain PRE header is truncated: expected {HeaderSize} bytes, found {stream.Length}.");

        var entryCount = reader.ReadUInt32();

        // The plain-v1 layout has NO magic, so it is the fall-through for
        // anything IsCompressedPre declines. A compressed PRE whose version
        // dword is 0xABCD-shaped but UNKNOWN (a future 0xABCD0005) would land
        // here and garbage-parse: its first dword is totalFileSize, not an
        // entry count. Refuse it explicitly instead (guard added 2026-08-04
        // while bringing up the THPS3/THPS4 PS1 corpus).
        if (stream.Length >= 8)
        {
            var maybeVersion = reader.ReadUInt32();
            stream.Position -= 4;
            if ((maybeVersion & 0xFFFF0000u) == 0xABCD0000u)
            {
                throw new InvalidDataException(
                    $"Unsupported compressed-PRE version 0x{maybeVersion:X8} " +
                    "(expected 0xABCD0002 through 0xABCD0004); refusing the plain-PRE fallback.");
            }
        }

        var maximumEntryCount = (stream.Length - HeaderSize) / MinimumEntrySize;
        if (entryCount > int.MaxValue || entryCount > maximumEntryCount)
            throw new InvalidDataException(
                $"Plain PRE entry count {entryCount} cannot fit in {stream.Length} bytes.");

        var entries = new List<ArchiveEntry>((int)entryCount);

        for (var i = 0; i < (int)entryCount; i++)
        {
            AlignTo4(stream, $"entry {i} name");

            var name = ReadNullTerminatedString(reader, i);

            AlignTo4(stream, $"entry {i} size");
            EnsureAvailable(stream, sizeof(uint), $"entry {i} size");

            var dataSize = reader.ReadUInt32();
            var dataOffset = stream.Position;

            EnsureAvailable(stream, dataSize, $"entry {i} payload");

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Size = dataSize,
                Offset = dataOffset
            });

            stream.Position = dataOffset + dataSize;
        }

        return entries;
    }

    public static void ExtractFiles(string prePath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var entries = GetFileList(prePath);
        var archiveName = ArchiveNaming.GetExtractionStem(prePath);
        var outputRoot = Path.GetFullPath(outputDir);
        var extractionRoot = ArchiveExtractionPath.GetContainedPath(
            outputRoot, archiveName, "PRE extraction directory");
        var exportPaths = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            exportPaths[i] = ArchiveExtractionPath.GetContainedPath(
                extractionRoot, entries[i].Name, "PRE entry");
        }

        using var stream = File.OpenRead(prePath);

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

    private static void AlignTo4(Stream stream, string context)
    {
        var remainder = stream.Position % 4;
        if (remainder != 0)
        {
            var paddingSize = 4 - remainder;
            EnsureAvailable(stream, paddingSize, $"{context} alignment padding");
            stream.Position += paddingSize;
        }
    }

    private static string ReadNullTerminatedString(BinaryReader reader, int entryIndex)
    {
        var bytes = new List<byte>();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var b = reader.ReadByte();
            if (b == 0)
                return Encoding.ASCII.GetString(bytes.ToArray());

            bytes.Add(b);
        }

        throw new InvalidDataException($"Plain PRE entry {entryIndex} name is not null-terminated.");
    }

    private static void EnsureAvailable(Stream stream, long byteCount, string context)
    {
        if (stream.Position > stream.Length || byteCount > stream.Length - stream.Position)
            throw new InvalidDataException($"Plain PRE {context} extends past the end of the archive.");
    }
}
