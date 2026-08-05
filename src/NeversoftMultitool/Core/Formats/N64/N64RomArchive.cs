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
///     Only the BOOT package uses that table shape. The asset corpus is
///     STANDALONE ERZ blocks packed back-to-back (one block = one asset, every
///     asset-region block decodes to &lt; 64 KB; measured across all four ROMs
///     2026-08-05), enumerated here by aligned magic scan outside the table
///     spans. Nothing carries names, so entries are offset-named
///     (<c>0x0013B74.bin</c>) — the same convention as unresolved hashed-HED
///     entries. Extraction decodes ERZ v2 blocks; v1 blocks (the THPS1/2/3
///     asset regions) and stored blocks are copied raw until the v1 core is
///     transcribed — Spider-Man's corpus is all-v2 and decodes completely.
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
        for (var position = 0x1000; position + ErzDecoder.HeaderSize <= rom.Length; position += 2)
        {
            if (!ErzDecoder.IsErz(rom.AsSpan(position, ErzDecoder.HeaderSize)))
                continue;
            if (covered.Any(range => position >= range.Start && position < range.End))
                continue;

            var compressed = ErzDecoder.GetCompressedSize(rom.AsSpan(position));
            var length = ErzDecoder.HeaderSize + compressed;
            if (compressed <= 0 || position + length > rom.Length)
                continue;
            var decompressed = ErzDecoder.GetDecompressedSize(rom.AsSpan(position));
            if (decompressed is <= 0 or > (1 << 24))
                continue;

            blocks.Add((position, length));
            position += length - 2;
            position &= ~1;
        }

        return blocks;
    }

    /// <summary>Decodes one table's blocks into the logical file they form.</summary>
    public static byte[] ExtractTable(byte[] rom, SubFileTable table)
    {
        using var output = new MemoryStream();
        foreach (var (offset, length) in table.Blocks)
        {
            var block = rom[offset..(offset + length)];
            if (ErzDecoder.IsErz(block))
            {
                try
                {
                    output.Write(ErzDecoder.Decode(block));
                    continue;
                }
                catch (NotSupportedException)
                {
                    // ERZ v1 — not transcribed yet; keep the raw block so the
                    // extraction is at least complete and re-runnable later.
                }
            }

            output.Write(block);
        }

        return output.ToArray();
    }

    public static List<ArchiveEntry> GetFileList(string romPath)
    {
        var rom = File.ReadAllBytes(romPath);
        var entries = new List<ArchiveEntry>();
        var tables = FindTables(rom);
        foreach (var (offset, length) in FindStandaloneBlocks(rom, tables))
        {
            entries.Add(new ArchiveEntry
            {
                Name = $"0x{offset:X7}.bin",
                Offset = offset,
                Size = ErzDecoder.GetVersion(rom.AsSpan(offset)) == 2
                    ? ErzDecoder.GetDecompressedSize(rom.AsSpan(offset))
                    : length
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

    public static void ExtractFiles(
        string romPath,
        string outputDir,
        Action<int, int>? onFileExtracted = null,
        CancellationToken token = default)
    {
        var rom = File.ReadAllBytes(romPath);
        var tables = FindTables(rom);
        var standalone = FindStandaloneBlocks(rom, tables);
        Directory.CreateDirectory(outputDir);
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
            byte[] data;
            if (ErzDecoder.GetVersion(block) == 2)
            {
                data = ErzDecoder.Decode(block);
            }
            else
            {
                // ERZ v1 — core not transcribed yet; keep the raw block so
                // extraction is complete and re-runnable once it is.
                data = block;
            }

            File.WriteAllBytes(Path.Combine(outputDir, $"0x{offset:X7}.bin"), data);
            onFileExtracted?.Invoke(++done, total);
        }
    }
}
