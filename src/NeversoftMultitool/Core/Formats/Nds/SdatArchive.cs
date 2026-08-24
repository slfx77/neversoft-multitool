using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Nds;

/// <summary>
///     Nintendo Nitro SDK <c>SDAT</c> sound archive. American Sk8land ships one in
///     its Nitro filesystem — <c>vvobj/generated/sound/sound_stream.sdat</c>, 40 MB
///     holding the game's whole 62-minute soundtrack as 30 <see cref="StrmFile" />
///     streams.
///
///     Four blocks, located by the 16-byte file header's block table:
///     <c>SYMB</c> (names), <c>INFO</c> (per-record metadata), <c>FAT </c>
///     (<c>{offset, size}</c> per member) and <c>FILE</c> (the payloads). Members
///     are listed under a directory named for the record type that owns them
///     (<c>strm/</c>, <c>wave/</c>, …), taking the SYMB name where the archive
///     supplies one and the FAT ordinal where it does not.
///
///     Detection is structural, not just the magic: the block table has to be
///     in-bounds and the FAT has to describe members that fit inside the file.
/// </summary>
public static class SdatArchive
{
    private const int HeaderSize = 16;
    private const int MinSize = 64;

    /// <summary>INFO/SYMB record kinds, in their fixed table order.</summary>
    private static readonly string[] RecordKinds =
        ["seq", "seqarc", "bank", "wave", "player", "group", "player2", "strm"];

    public static bool IsSdat(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length is < MinSize or > int.MaxValue)
                return false;
            var header = new byte[HeaderSize];
            stream.ReadExactly(header);
            return IsSdatHeader(header, stream.Length);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsSdat(ReadOnlySpan<byte> data)
    {
        return data.Length >= MinSize && IsSdatHeader(data, data.Length);
    }

    private static bool IsSdatHeader(ReadOnlySpan<byte> header, long fileLength)
    {
        if (!header[..4].SequenceEqual("SDAT"u8))
            return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 0xFEFF)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != fileLength)
            return false;
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        var blocks = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        return headerSize >= HeaderSize && blocks is > 0 and <= 8
                                        && headerSize + (long)blocks * 8 <= fileLength;
    }

    /// <summary>Classification string for the detector ("SDAT sound archive" or null).</summary>
    public static string? ClassifyArchive(string path)
    {
        return IsSdat(path) ? "SDAT" : null;
    }

    public static List<ArchiveEntry> GetFileList(string path)
    {
        return BuildFileList(File.ReadAllBytes(path));
    }

    public static List<ArchiveEntry> BuildFileList(byte[] data)
    {
        if (!IsSdat(data))
            throw new InvalidDataException("Not a Nitro SDAT sound archive.");

        var blocks = ReadBlocks(data);
        if (!blocks.TryGetValue("FAT ", out var fat))
            throw new InvalidDataException("SDAT has no FAT block.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fat + 8));
        if (count > (data.Length - fat) / 16)
            throw new InvalidDataException($"SDAT FAT declares {count} members, which cannot fit.");

        var names = ReadNames(data, blocks);
        var owners = ReadOwners(data, blocks, (int)count);

        var entries = new List<ArchiveEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var record = fat + 12 + i * 16;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 4));
            if (offset > (uint)data.Length || size > data.Length - offset)
                throw new InvalidDataException($"SDAT member {i} runs outside the archive.");

            var found = owners.TryGetValue(i, out var owned);
            var kind = found ? owned.Kind : "file";
            var name = (found ? owned.Name : null) ?? $"{kind}_{i:D4}";
            entries.Add(new ArchiveEntry
            {
                Directory = kind,
                Name = name + ExtensionFor(data, offset, size),
                Offset = offset,
                Size = size
            });
        }

        return entries;
    }

    private static string ExtensionFor(byte[] data, uint offset, uint size)
    {
        if (size < 4)
            return ".bin";
        var magic = data.AsSpan((int)offset, 4);
        if (magic.SequenceEqual("STRM"u8)) return ".strm";
        if (magic.SequenceEqual("SWAV"u8)) return ".swav";
        if (magic.SequenceEqual("SWAR"u8)) return ".swar";
        if (magic.SequenceEqual("SBNK"u8)) return ".sbnk";
        if (magic.SequenceEqual("SSEQ"u8)) return ".sseq";
        if (magic.SequenceEqual("SSAR"u8)) return ".ssar";
        return ".bin";
    }

    private static Dictionary<string, int> ReadBlocks(byte[] data)
    {
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
        var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        var blocks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < blockCount; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderSize + i * 8));
            if (offset + 8 > (uint)data.Length)
                continue;
            blocks[Encoding.ASCII.GetString(data, (int)offset, 4)] = (int)offset;
        }

        _ = headerSize;
        return blocks;
    }

    /// <summary>SYMB names per record kind, indexed by record number.</summary>
    private static Dictionary<string, List<string?>> ReadNames(byte[] data, Dictionary<string, int> blocks)
    {
        var result = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        if (!blocks.TryGetValue("SYMB", out var symb))
            return result;

        for (var k = 0; k < RecordKinds.Length; k++)
        {
            var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(symb + 8 + k * 4));
            if (tableOffset == 0 || symb + tableOffset + 4 > data.Length)
                continue;
            var at = symb + (int)tableOffset;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            if (count > (data.Length - at) / 4)
                continue;

            var list = new List<string?>((int)count);
            for (var i = 0; i < count; i++)
            {
                var pointer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4 + i * 4));
                list.Add(pointer == 0 ? null : ReadCString(data, symb + (int)pointer));
            }

            result[RecordKinds[k]] = list;
        }

        return result;
    }

    /// <summary>
    ///     Maps a FAT index to the record that owns it, so a member can be filed
    ///     under its kind and take that record's SYMB name. INFO records begin with
    ///     a u16 FAT index for every kind that owns file data.
    /// </summary>
    private static Dictionary<int, (string Kind, string? Name)> ReadOwners(
        byte[] data, Dictionary<string, int> blocks, int fatCount)
    {
        var owners = new Dictionary<int, (string, string?)>();
        if (!blocks.TryGetValue("INFO", out var info))
            return owners;

        var names = ReadNames(data, blocks);
        for (var k = 0; k < RecordKinds.Length; k++)
        {
            var kind = RecordKinds[k];
            var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(info + 8 + k * 4));
            if (tableOffset == 0 || info + tableOffset + 4 > data.Length)
                continue;
            var at = info + (int)tableOffset;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            if (count > (data.Length - at) / 4)
                continue;

            names.TryGetValue(kind, out var kindNames);
            for (var i = 0; i < count; i++)
            {
                var recordOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4 + i * 4));
                if (recordOffset == 0 || info + recordOffset + 2 > data.Length)
                    continue;
                var fileId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(info + (int)recordOffset));
                if (fileId >= fatCount || owners.ContainsKey(fileId))
                    continue;
                var name = kindNames != null && i < kindNames.Count ? kindNames[i] : null;
                owners[fileId] = (kind, name);
            }
        }

        return owners;
    }

    private static string ReadCString(byte[] data, int at)
    {
        if (at < 0 || at >= data.Length)
            return "";
        var end = Array.IndexOf(data, (byte)0, at);
        if (end < 0)
            end = data.Length;
        return Encoding.ASCII.GetString(data, at, end - at);
    }

    public static void ExtractFiles(
        string archivePath,
        string outputDir,
        Action<int, int>? onFileExtracted = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var data = File.ReadAllBytes(archivePath);
        var entries = BuildFileList(data);
        Directory.CreateDirectory(outputDir);

        var done = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var target = ArchiveExtractionPath.GetContainedPath(outputDir, entry.FullName, "SDAT member");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data.AsSpan((int)entry.Offset, (int)entry.Size).ToArray());
            onFileExtracted?.Invoke(++done, entries.Count);
        }
    }
}
