using System.Text;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
/// Extracts files from Neversoft PRE v2/v3 archives with LZSS compression.
/// Used in THPS3, THPS4, THUG, THUG2, and THAW (PS2, Xbox, GameCube).
/// Xbox variant uses .prx extension. Format documented in THUG source: Sys/File/PRE.cpp.
///
/// Header (12 bytes): totalFileSize(i32) + version(i32) + numEntries(i32)
/// V2 per entry: dataSize(i32) + compressedDataSize(i32) + nameSize(i16) + reserved(i16)
///               + name(nameSize bytes) + data(pad4(actualSize))
/// V3 per entry: dataSize(i32) + compressedDataSize(i32) + nameSize(i16) + reserved(i16)
///               + checksum(u32) + name(nameSize bytes) + data(pad4(actualSize))
/// </summary>
public static class CompressedPreArchive
{
    private const uint VersionV2 = 0xABCD0002; // THPS3/THPS4 (no checksum field)
    private const uint VersionV3 = 0xABCD0003; // THUG+ (has checksum field)
    private const int HeaderSize = 12;

    /// <summary>
    /// Returns true if the file is a compressed PRE archive (v2/v3, has 0xABCDxxxx version).
    /// </summary>
    public static bool IsCompressedPre(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        if (stream.Length < HeaderSize) return false;
        using var reader = new BinaryReader(stream);
        reader.ReadInt32(); // totalFileSize
        var version = reader.ReadUInt32();
        return version is VersionV2 or VersionV3;
    }

    public static List<ArchiveEntry> GetFileList(string prePath)
    {
        using var stream = File.OpenRead(prePath);
        using var reader = new BinaryReader(stream);

        var totalFileSize = reader.ReadInt32();
        var version = reader.ReadUInt32();
        if (version is not VersionV2 and not VersionV3)
            throw new InvalidDataException(
                $"Not a PRE v3 archive: version 0x{version:X8} (expected 0xABCD0002 or 0xABCD0003)");

        var numEntries = reader.ReadInt32();
        var hasChecksum = version == VersionV3;
        var entries = new List<ArchiveEntry>(numEntries);

        for (var i = 0; i < numEntries; i++)
        {
            var entryStart = stream.Position;

            var dataSize = reader.ReadInt32();
            var compressedDataSize = reader.ReadInt32();
            var nameSize = reader.ReadInt16();
            var reserved = reader.ReadInt16(); // usage count slot, unused on disk
            var checksum = hasChecksum ? reader.ReadUInt32() : 0u;

            var nameBytes = reader.ReadBytes(nameSize);
            var nullIndex = Array.IndexOf(nameBytes, (byte)0);
            var name = Encoding.ASCII.GetString(nameBytes, 0, nullIndex >= 0 ? nullIndex : nameBytes.Length);

            var actualDataSize = compressedDataSize != 0 ? compressedDataSize : dataSize;
            var dataOffset = stream.Position;

            // Normalize path separators and strip leading slash
            name = name.Replace('\\', '/').TrimStart('/');

            // Split directory and filename
            var directory = "";
            var fileName = name;
            var lastSlash = name.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                directory = name[..lastSlash];
                fileName = name[(lastSlash + 1)..];
            }

            entries.Add(new ArchiveEntry
            {
                Name = fileName,
                Directory = directory,
                Size = dataSize,
                Offset = dataOffset,
                IsCompressed = compressedDataSize != 0,
                CompressedSize = compressedDataSize,
                Crc = checksum
            });

            // Advance to next entry: data + pad to 4-byte alignment
            var paddedDataSize = (actualDataSize + 3) & ~3;
            stream.Position = dataOffset + paddedDataSize;
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

            var actualDataSize = entry.IsCompressed ? (int)entry.CompressedSize : (int)entry.Size;
            var rawData = new byte[actualDataSize];
            stream.ReadExactly(rawData);

            byte[] outputData;
            if (entry.IsCompressed)
                outputData = LzssDecoder.Decode(rawData, (int)entry.Size);
            else
                outputData = rawData;

            var exportPath = Path.Combine(outputDir, archiveName, entry.FullName);
            var exportDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(exportDir))
                Directory.CreateDirectory(exportDir);

            File.WriteAllBytes(exportPath, outputData);

            onFileExtracted?.Invoke(i + 1, entries.Count);
        }
    }
}
