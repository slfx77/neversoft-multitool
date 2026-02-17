using System.Text;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
/// Extracts files from Neversoft PRE archives.
/// PRE is a simple flat archive format with no compression, used in THPS1 (PS1),
/// THPS2 (PS1 and Dreamcast). Contains BMP images, fonts, PSX models, and scripts.
/// </summary>
public static class PreArchive
{
    public static List<ArchiveEntry> GetFileList(string prePath)
    {
        using var stream = File.OpenRead(prePath);
        using var reader = new BinaryReader(stream);

        var entryCount = reader.ReadUInt32();
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
        var archiveName = Path.GetFileNameWithoutExtension(prePath);

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
