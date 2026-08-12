using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.N64;

/// <summary>
///     Read-only archive view over an N64 <c>.z64</c> ROM (THPS1/2/3 +
///     Spider-Man, Edge of Reality). These carts have no OS filesystem; assets
///     live in SUB-FILE TABLES the boot code streams: a big-endian
///     <c>u32 count</c> followed by <c>count+1</c> ascending offsets relative
///     to the table start, whose first offset equals the table header size
///     (<c>4 + 4*(count+1)</c>). Each entry is either an <see cref="ErzDecoder">
///     ERZ</see>-compressed 64 KB block or stored raw; a table's blocks
///     concatenate into one logical file (the boot loop writes block N at
///     <c>dst + N*0x10000</c>).
///
///     The authoritative enumeration is the ROM's own MASTER DIRECTORY
///     (RE'd 2026-08-05, verified on all four ROMs): the BE u32 immediately
///     BEFORE the boot table is a KSEG1 cart pointer (<c>0xB0......</c>);
///     masked with <c>0x0FFFFFFF</c> it addresses a root table of stream-group
///     directories (THPS1 7, THPS2/THPS3 9, Spider-Man 8; root offs[0] may
///     carry a few pad bytes). Each group directory uses the standard table
///     shape with NON-DECREASING offsets — equal neighbours are authored
///     empty slots. A compressed group is ONE logical stream cut into 16 KB
///     ERZ decompression units for random access (block index =
///     decompressedOffset &gt;&gt; 14; final block short); uncompressed groups
///     (audio wavetables/banks) store one payload per leaf. The tree covers
///     every ERZ block in the ROM plus the raw audio the magic scan missed,
///     including blocks at odd offsets the aligned scan skipped (Spider-Man
///     ships 4). Extraction carves the reassembled streams into typed assets
///     via <see cref="N64AssetCarver" />; the flat magic scan remains as the
///     fallback for ROMs without the directory.
///
///     Byte order gate: <c>.z64</c> big-endian (magic <c>80 37 12 40</c>) only.
///     Byte-swapped <c>.v64</c> and little-endian <c>.n64</c> dumps are
///     detected and rejected with an explicit message rather than mis-parsed.
/// </summary>
public static class N64RomArchive
{
    private const uint Z64Magic = 0x80371240;
    private const uint V64Magic = 0x37804012;
    private const uint N64Magic = 0x40123780;
    private const int MaxTableEntries = 4095;

    public static bool IsN64Rom(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 0x1000)
            return false;
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        return BinaryPrimitives.ReadUInt32BigEndian(magic) == Z64Magic;
    }

    /// <summary>Classification string for the detector ("N64 ROM", byte-order variants, or null).</summary>
    public static string? ClassifyRom(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 4)
            return null;
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        return BinaryPrimitives.ReadUInt32BigEndian(magic) switch
        {
            Z64Magic => "N64 ROM",
            V64Magic => "N64 ROM (byteswapped .v64 — re-dump as .z64)",
            N64Magic => "N64 ROM (little-endian .n64 — re-dump as .z64)",
            _ => null
        };
    }

    public readonly record struct SubFileTable(int Offset, IReadOnlyList<(int Offset, int Length)> Blocks)
    {
        public int DataLength => Blocks.Count == 0 ? 0 : Blocks[^1].Offset + Blocks[^1].Length - Blocks[0].Offset;
    }

    /// <summary>
    ///     Finds every sub-file table in the ROM. Validation is deliberately
    ///     strict — the shape must hold exactly AND at least one entry must
    ///     carry the ERZ magic, so ascending-integer runs inside compressed
    ///     data cannot masquerade as tables.
    /// </summary>
    public static List<SubFileTable> FindTables(byte[] rom)
    {
        var tables = new List<SubFileTable>();
        var position = 0x1000; // skip header + IPL3 boot code
        while (position + 12 <= rom.Length)
        {
            var table = TryReadTable(rom, position);
            if (table is { } found)
            {
                tables.Add(found);
                position += found.Blocks[^1].Offset - found.Offset + found.Blocks[^1].Length;
                position = (position + 3) & ~3;
            }
            else
            {
                position += 4;
            }
        }

        return tables;
    }

    private static SubFileTable? TryReadTable(byte[] rom, int position)
    {
        var count = BinaryPrimitives.ReadInt32BigEndian(rom.AsSpan(position));
        if (count is < 1 or > MaxTableEntries)
            return null;

        var headerSize = 4 + 4 * (count + 1);
        if (position + headerSize > rom.Length)
            return null;

        var offsets = new int[count + 1];
        for (var k = 0; k <= count; k++)
        {
            offsets[k] = BinaryPrimitives.ReadInt32BigEndian(rom.AsSpan(position + 4 + 4 * k));
            if (offsets[k] < headerSize
                || (k > 0 && offsets[k] <= offsets[k - 1])
                || position + offsets[k] > rom.Length)
            {
                return null;
            }
        }

        if (offsets[0] != headerSize)
            return null;

        var blocks = new List<(int Offset, int Length)>(count);
        var sawErz = false;
        for (var k = 0; k < count; k++)
        {
            var blockOffset = position + offsets[k];
            var blockLength = offsets[k + 1] - offsets[k];
            blocks.Add((blockOffset, blockLength));
            if (blockLength >= ErzDecoder.HeaderSize
                && ErzDecoder.IsErz(rom.AsSpan(blockOffset, ErzDecoder.HeaderSize)))
            {
                sawErz = true;
            }
        }

        return sawErz ? new SubFileTable(position, blocks) : null;
    }

    /// <summary>
    ///     Standalone ERZ blocks outside the given tables: aligned magic scan,
    ///     each block one entry. Returns (offset, blockLength) pairs.
    /// </summary>
    public static List<(int Offset, int Length)> FindStandaloneBlocks(
        byte[] rom,
        IReadOnlyList<SubFileTable> tables)
    {
        var covered = new List<(int Start, int End)>();
        foreach (var table in tables)
        {
            covered.Add((table.Offset,
                table.Blocks.Count == 0 ? table.Offset : table.Blocks[^1].Offset + table.Blocks[^1].Length));
        }

        covered.Sort();
        var blocks = new List<(int, int)>();
        var position = 0x1000;
        while (position + ErzDecoder.HeaderSize <= rom.Length)
        {
            if (!ErzDecoder.IsErz(rom.AsSpan(position, ErzDecoder.HeaderSize))
                || covered.Any(range => position >= range.Start && position < range.End))
            {
                position += 2;
                continue;
            }

            var compressed = ErzDecoder.GetCompressedSize(rom.AsSpan(position));
            var length = ErzDecoder.HeaderSize + compressed;
            var decompressed = ErzDecoder.GetDecompressedSize(rom.AsSpan(position));
            if (compressed <= 0 || position + length > rom.Length || decompressed is <= 0 or > (1 << 24))
            {
                position += 2;
                continue;
            }

            blocks.Add((position, length));
            position = (position + length) & ~1;
        }

        return blocks;
    }

    public readonly record struct StreamGroup(
        int Index,
        int DirectoryOffset,
        IReadOnlyList<(int Offset, int Length)> Leaves,
        bool IsCompressed);

    /// <summary>
    ///     Walks the master asset directory: boot table located by scan, the
    ///     u32 before it is the KSEG1 pointer to the root, root children are
    ///     the per-stream block directories. False when any link fails — the
    ///     caller falls back to the flat scan.
    /// </summary>
    public static bool TryReadMasterDirectory(
        byte[] rom,
        out int rootOffset,
        out List<StreamGroup> groups,
        out SubFileTable bootTable)
    {
        rootOffset = 0;
        groups = [];
        bootTable = default;

        if (FindBootTable(rom) is not { } boot || boot.Offset < 4)
            return false;

        var pointer = BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(boot.Offset - 4));
        if (pointer >> 28 != 0xB)
            return false;

        var root = (int)(pointer & 0x0FFFFFFF);
        // Root tolerates pad bytes between the offset array and the first
        // child (THPS2 ships 4); children are strict.
        var rootOffsets = TryReadDirectory(rom, root, slack: 16);
        if (rootOffsets == null || rootOffsets.Length - 1 > 64)
            return false;

        var result = new List<StreamGroup>();
        for (var g = 0; g < rootOffsets.Length - 1; g++)
        {
            var directory = root + rootOffsets[g];
            var offsets = TryReadDirectory(rom, directory, slack: 0);
            if (offsets == null)
                return false;

            var leaves = new List<(int Offset, int Length)>(offsets.Length - 1);
            var compressed = false;
            for (var k = 0; k < offsets.Length - 1; k++)
            {
                var offset = directory + offsets[k];
                var length = offsets[k + 1] - offsets[k];
                leaves.Add((offset, length));
                if (!compressed
                    && length >= ErzDecoder.HeaderSize
                    && ErzDecoder.IsErz(rom.AsSpan(offset, ErzDecoder.HeaderSize)))
                {
                    compressed = true;
                }
            }

            result.Add(new StreamGroup(g, directory, leaves, compressed));
        }

        rootOffset = root;
        groups = result;
        bootTable = boot;
        return true;
    }

    /// <summary>First strict sub-file table in the ROM = the boot package.</summary>
    private static SubFileTable? FindBootTable(byte[] rom)
    {
        for (var position = 0x1000; position + 12 <= rom.Length; position += 4)
        {
            if (TryReadTable(rom, position) is { } table)
                return table;
        }

        return null;
    }

    /// <summary>
    ///     Directory-table read: BE count + count+1 NON-DECREASING offsets
    ///     (equal neighbours = authored empty slots, e.g. THPS3's audio group
    ///     leads with 8). offs[0] must land within <c>slack</c> bytes of the
    ///     strict header size.
    /// </summary>
    private static int[]? TryReadDirectory(byte[] rom, int position, int slack)
    {
        if (position < 0 || position + 8 > rom.Length)
            return null;
        var count = BinaryPrimitives.ReadInt32BigEndian(rom.AsSpan(position));
        if (count is < 0 or > MaxTableEntries)
            return null;
        var headerSize = 4 + 4 * (count + 1);
        if (position + headerSize > rom.Length)
            return null;

        var offsets = new int[count + 1];
        for (var k = 0; k <= count; k++)
        {
            offsets[k] = BinaryPrimitives.ReadInt32BigEndian(rom.AsSpan(position + 4 + 4 * k));
            if ((k > 0 && offsets[k] < offsets[k - 1]) || position + offsets[k] > rom.Length)
                return null;
        }

        if (offsets[0] < headerSize || offsets[0] > headerSize + slack)
            return null;
        return offsets;
    }

    /// <summary>Decodes one table's blocks into the logical file they form.</summary>
    public static byte[] ExtractTable(byte[] rom, SubFileTable table)
    {
        using var output = new MemoryStream();
        foreach (var (offset, length) in table.Blocks)
        {
            var block = rom[offset..(offset + length)];
            output.Write(ErzDecoder.IsErz(block) ? ErzDecoder.Decode(block) : block);
        }

        return output.ToArray();
    }

    public static List<ArchiveEntry> GetFileList(string romPath)
    {
        var rom = File.ReadAllBytes(romPath);
        if (N64AssetCarver.TryCarve(rom, out var assets))
        {
            return assets.Select(static asset => new ArchiveEntry
            {
                Name = asset.Path,
                Size = asset.Data.Length
            }).ToList();
        }

        return LegacyFileList(rom);
    }

    public static void ExtractFiles(
        string romPath,
        string outputDir,
        Action<int, int>? onFileExtracted = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        var rom = File.ReadAllBytes(romPath);
        Directory.CreateDirectory(outputDir);
        if (N64AssetCarver.TryCarve(rom, out var assets))
        {
            var done = 0;
            foreach (var asset in assets)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(outputDir, asset.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, asset.Data);
                onFileExtracted?.Invoke(++done, assets.Count);
            }

            return;
        }

        LegacyExtract(rom, outputDir, onFileExtracted, token);
    }

    /// <summary>Flat scan listing for ROMs without a master directory.</summary>
    private static List<ArchiveEntry> LegacyFileList(byte[] rom)
    {
        var entries = new List<ArchiveEntry>();
        var tables = FindTables(rom);
        foreach (var (offset, _) in FindStandaloneBlocks(rom, tables))
        {
            entries.Add(new ArchiveEntry
            {
                Name = $"0x{offset:X7}.bin",
                Offset = offset,
                Size = ErzDecoder.GetDecompressedSize(rom.AsSpan(offset))
            });
        }

        foreach (var table in tables)
        {
            long size = 0;
            foreach (var (offset, length) in table.Blocks)
            {
                var span = rom.AsSpan(offset, Math.Min(length, ErzDecoder.HeaderSize));
                size += span.Length >= ErzDecoder.HeaderSize && ErzDecoder.IsErz(span)
                    ? ErzDecoder.GetDecompressedSize(rom.AsSpan(offset))
                    : length;
            }

            entries.Add(new ArchiveEntry
            {
                Name = $"0x{table.Offset:X7}.bin",
                Offset = table.Offset,
                Size = size
            });
        }

        return entries;
    }

    private static void LegacyExtract(
        byte[] rom,
        string outputDir,
        Action<int, int>? onFileExtracted,
        CancellationToken token)
    {
        var tables = FindTables(rom);
        var standalone = FindStandaloneBlocks(rom, tables);
        var total = tables.Count + standalone.Count;
        var done = 0;
        foreach (var table in tables)
        {
            token.ThrowIfCancellationRequested();
            File.WriteAllBytes(
                Path.Combine(outputDir, $"0x{table.Offset:X7}.bin"),
                ExtractTable(rom, table));
            onFileExtracted?.Invoke(++done, total);
        }

        foreach (var (offset, length) in standalone)
        {
            token.ThrowIfCancellationRequested();
            var block = rom[offset..(offset + length)];
            File.WriteAllBytes(Path.Combine(outputDir, $"0x{offset:X7}.bin"), ErzDecoder.Decode(block));
            onFileExtracted?.Invoke(++done, total);
        }
    }
}
