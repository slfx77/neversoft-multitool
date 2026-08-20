using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Nds;

/// <summary>
///     Read-only archive view over a Nintendo DS <c>.nds</c> ROM. Unlike the N64
///     carts, DS ROMs carry a real filesystem (Nitro FS), so entries are plain
///     contiguous byte ranges — the disk-backed
///     <see cref="ArchiveFs.FileArchiveFileSystem" /> serves them directly with
///     no carve/decompress step.
///
///     Header layout (little-endian, GBATEK): title[12]@0x00, gameCode[4]@0x0C,
///     ARM9 <c>{romOff, entry, ram, size}</c>@0x20 and ARM7@0x30, then
///     <c>{fntOff, fntSize, fatOff, fatSize}</c>@0x40 and the overlay tables
///     <c>{ov9Off, ov9Size, ov7Off, ov7Size}</c>@0x50. The FAT is 8-byte
///     <c>{start, end}</c> slots; the FNT is a directory main-table of 8-byte
///     records <c>{subTableOffset, firstFileId, parentId|dirCount}</c> followed
///     by name sub-tables (1-byte length+flag, bit7 = directory, then the name,
///     then a u16 child-dir id for directories).
///
///     Alongside the Nitro tree the listing exposes the loader components under
///     <c>_system/</c> — header, ARM9/ARM7 binaries, the banner, and the ARM9
///     overlays — because the Vicarious Visions GOB container loader lives in
///     ARM9/overlay code and reverse-engineering the GOB format needs it. Those
///     names cannot collide: these carts' Nitro roots hold a handful of files
///     under real directories (<c>vvobj/</c>, <c>gob/</c>, <c>bink/</c>, …).
///
///     Detection gates on the fixed Nintendo-logo CRC16 (<c>0xCF56</c> for every
///     commercial cart) plus the header CRC16 and in-bounds FNT/FAT fields, so a
///     truncated or non-DS file is rejected rather than mis-parsed.
/// </summary>
public static class NdsRomArchive
{
    private const int HeaderSize = 0x200;
    private const int LogoCrcOffset = 0x15C;
    private const int HeaderCrcOffset = 0x15E;
    private const ushort CommercialLogoCrc = 0xCF56;
    private const int MinRomSize = 0x8000;

    public static bool IsNdsRom(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length is < MinRomSize or > 0x20000000)
                return false;
            var header = new byte[HeaderSize];
            stream.ReadExactly(header);
            return IsNdsHeader(header, stream.Length);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Classification string for the detector ("NDS ROM" or null).</summary>
    public static string? ClassifyRom(string path)
    {
        return IsNdsRom(path) ? "NDS ROM" : null;
    }

    private static bool IsNdsHeader(byte[] header, long fileLength)
    {
        if (header.Length < HeaderSize)
            return false;

        // Fixed logo CRC is the strongest, cheapest magic; the header CRC then
        // confirms the whole header is intact.
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(LogoCrcOffset)) != CommercialLogoCrc)
            return false;
        if (Crc16(header.AsSpan(0, HeaderCrcOffset)) !=
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(HeaderCrcOffset)))
            return false;

        var fntOff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x40));
        var fntSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x44));
        var fatOff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x48));
        var fatSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x4C));

        if ((fntOff & 3) != 0 || (fatOff & 3) != 0)
            return false;
        if (fatSize == 0 || fatSize % 8 != 0)
            return false;
        if (fntOff < HeaderSize || fatOff < HeaderSize)
            return false;
        if (fntOff + fntSize > fileLength || fatOff + fatSize > fileLength)
            return false;
        return true;
    }

    public static List<ArchiveEntry> GetFileList(string path)
    {
        var rom = File.ReadAllBytes(path);
        return BuildFileList(rom);
    }

    public static List<ArchiveEntry> BuildFileList(byte[] rom)
    {
        if (!IsNdsHeader(rom, rom.LongLength))
            throw new InvalidDataException("Not a Nintendo DS ROM (header/logo CRC or FNT/FAT check failed).");

        var entries = new List<ArchiveEntry>();
        AddSystemEntries(rom, entries);
        AddNitroEntries(rom, entries);
        return entries;
    }

    private static void AddSystemEntries(byte[] rom, List<ArchiveEntry> entries)
    {
        void AddRange(string name, long offset, long size)
        {
            if (size <= 0 || offset < 0 || offset + size > rom.LongLength)
                return;
            entries.Add(new ArchiveEntry { Directory = "_system", Name = name, Offset = offset, Size = size });
        }

        AddRange("header.bin", 0, HeaderSize);

        // ARM9 block: {romOff@0x20, entry@0x24, ram@0x28, size@0x2C}; ARM7 at 0x30.
        var arm9Off = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x20));
        var arm9Size = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x2C));
        AddRange("arm9.bin", arm9Off, arm9Size);

        var arm7Off = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x30));
        var arm7Size = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x3C));
        AddRange("arm7.bin", arm7Off, arm7Size);

        var bannerOff = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x68));
        if (bannerOff != 0)
            AddRange("banner.bin", bannerOff, 0xA00); // standard v1 banner size

        AddOverlays(rom, entries, 0x50, "overlay9");
        AddOverlays(rom, entries, 0x58, "overlay7");
    }

    private static void AddOverlays(byte[] rom, List<ArchiveEntry> entries, int tableFieldOffset, string prefix)
    {
        var tableOff = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(tableFieldOffset));
        var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(tableFieldOffset + 4));
        if (tableOff == 0 || tableSize == 0 || tableOff + tableSize > rom.LongLength)
            return;

        var fat = ReadFat(rom, out _);
        for (long i = 0; i + 32 <= tableSize; i += 32)
        {
            var record = tableOff + i;
            var fileId = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan((int)record + 0x18));
            if (fileId >= (uint)fat.Count)
                continue;
            var (start, end) = fat[(int)fileId];
            if (start >= end || end > rom.LongLength)
                continue;
            entries.Add(new ArchiveEntry
            {
                Directory = "_system",
                Name = $"{prefix}_{fileId:D4}.bin",
                Offset = start,
                Size = end - start
            });
        }
    }

    private static List<(long Start, long End)> ReadFat(byte[] rom, out uint fatSize)
    {
        var fatOff = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x48));
        fatSize = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x4C));
        var slots = (int)(fatSize / 8);
        var fat = new List<(long, long)>(slots);
        for (var i = 0; i < slots; i++)
        {
            var start = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan((int)fatOff + i * 8));
            var end = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan((int)fatOff + i * 8 + 4));
            fat.Add((start, end));
        }

        return fat;
    }

    private static void AddNitroEntries(byte[] rom, List<ArchiveEntry> entries)
    {
        var fat = ReadFat(rom, out _);
        var fntOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x40));

        // Main table: record i = {u32 subTableOffset, u16 firstFileId, u16 parentId|dirCount}.
        // Record 0 (root) stores the total directory count in the last field.
        var dirCount = BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(fntOff + 6));
        if (dirCount is 0 or > 4096)
            return;

        var subOffset = new int[dirCount];
        var firstFile = new int[dirCount];
        var parentId = new int[dirCount];
        for (var i = 0; i < dirCount; i++)
        {
            var record = fntOff + i * 8;
            if (record + 8 > rom.Length)
                return;
            subOffset[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(record));
            firstFile[i] = BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(record + 4));
            parentId[i] = BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(record + 6));
        }

        // Walk each directory's sub-table once, collecting its own name (for
        // directories) and its files. Full paths are then assembled from the
        // parent chain (record 0 is the root, parent field is a count there).
        var localName = new string[dirCount]; // this dir's own name
        localName[0] = "";
        var files = new List<(int DirIndex, string Name, int FileId)>();
        for (var i = 0; i < dirCount; i++)
        {
            var pos = fntOff + subOffset[i];
            var fileId = firstFile[i];
            while (pos < rom.Length)
            {
                var length = rom[pos++];
                if (length == 0)
                    break;
                var isDir = (length & 0x80) != 0;
                var nameLen = length & 0x7F;
                if (pos + nameLen > rom.Length)
                    break;
                var name = System.Text.Encoding.ASCII.GetString(rom, pos, nameLen);
                pos += nameLen;
                if (isDir)
                {
                    if (pos + 2 > rom.Length)
                        break;
                    var childId = BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(pos)) & 0x0FFF;
                    pos += 2;
                    if (childId < dirCount)
                        localName[childId] = name;
                }
                else
                {
                    files.Add((i, name, fileId));
                    fileId++;
                }
            }
        }

        foreach (var (dirIndex, name, fileId) in files)
        {
            if (fileId >= fat.Count)
                continue;
            var (start, end) = fat[fileId];
            if (start > end || end > rom.LongLength)
                continue;
            entries.Add(new ArchiveEntry
            {
                Directory = BuildDirPath(dirIndex, localName, parentId),
                Name = name,
                Offset = start,
                Size = end - start
            });
        }
    }

    private static string BuildDirPath(int dirIndex, string[] localName, int[] parentId)
    {
        if (dirIndex == 0)
            return "";
        var parts = new List<string>();
        var current = dirIndex;
        var guard = 0;
        while (current > 0 && guard++ < localName.Length)
        {
            parts.Add(localName[current] ?? "");
            var parent = parentId[current] & 0x0FFF;
            if (parent == current || parent >= localName.Length)
                break;
            current = parent;
        }

        parts.Reverse();
        return string.Join('/', parts.Where(p => p.Length > 0));
    }

    public static void ExtractFiles(
        string romPath,
        string outputDir,
        Action<int, int>? onFileExtracted = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var rom = File.ReadAllBytes(romPath);
        var entries = BuildFileList(rom);
        Directory.CreateDirectory(outputDir);

        var done = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            // Real on-disc names — route through the containment guard.
            var target = ArchiveExtractionPath.GetContainedPath(outputDir, entry.FullName, "NDS entry");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var size = checked((int)entry.Size);
            var data = new byte[size];
            Array.Copy(rom, entry.Offset, data, 0, size);
            File.WriteAllBytes(target, data);
            onFileExtracted?.Invoke(++done, entries.Count);
        }
    }

    /// <summary>CRC16 (poly 0xA001, init 0xFFFF) as used for the DS header/logo checksums.</summary>
    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                var carry = crc & 1;
                crc >>= 1;
                if (carry != 0)
                    crc ^= 0xA001;
            }
        }

        return (ushort)crc;
    }
}
