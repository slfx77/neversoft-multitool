using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Extracts files from Neversoft PRE archives.
///     PRE is a simple flat archive format with no compression, used in THPS1 (PS1),
///     THPS2 (PS1 and Dreamcast). Contains BMP images, fonts, PSX models, and scripts.
/// </summary>
public static class PreArchive
{
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

        var entryCount = reader.ReadUInt32();

        // The plain-v1 layout has NO magic, so it is the fall-through for
        // anything IsCompressedPre declines. A compressed PRE whose version
        // dword is 0xABCD-shaped but UNKNOWN (a future 0xABCD0004) would land
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
                    "(expected 0xABCD0002 or 0xABCD0003); refusing the plain-PRE fallback.");
            }
        }

        var entries = new List<ArchiveEntry>((int)entryCount);

        for (var i = 0; i < entryCount; i++)
        {
            AlignTo4(stream);

            var name = ReadNullTerminatedString(reader);

            AlignTo4(stream);

            var dataSize = reader.ReadUInt32();
            var dataOffset = stream.Position;

            entries.Add(new ArchiveEntry
            {
                Name = name,
                Size = dataSize,
                Offset = dataOffset
            });

            stream.Seek(dataSize, SeekOrigin.Current);
        }

        return entries;
    }

    public static void ExtractFiles(string prePath, string outputDir,
        Action<int, int>? onFileExtracted = null, CancellationToken token = default)
    {
        var entries = GetFileList(prePath);
        var archiveName = ArchiveNaming.GetExtractionStem(prePath);

        using var stream = File.OpenRead(prePath);

        for (var i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var entry = entries[i];
            stream.Seek(entry.Offset, SeekOrigin.Begin);

            var exportPath = Path.Combine(outputDir, archiveName, entry.Name);
            var exportDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(exportDir))
                Directory.CreateDirectory(exportDir);

            var data = new byte[entry.Size];
            stream.ReadExactly(data);
            File.WriteAllBytes(exportPath, data);

            onFileExtracted?.Invoke(i + 1, entries.Count);
        }
    }

    private static void AlignTo4(Stream stream)
    {
        var remainder = stream.Position % 4;
        if (remainder != 0)
            stream.Seek(4 - remainder, SeekOrigin.Current);
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
}
