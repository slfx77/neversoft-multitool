using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Full-colour isometric level surfaces from Tony Hawk's Pro Skater 3 for
///     GBA. THPS3 predates the mixed 4bpp/8bpp parent record used by THPS4 and
///     later, but uses the same word-RLE map grammar.
/// </summary>
/// <remarks>
///     A 0x70-byte level record begins with the visible-art fields:
///     <c>{u16 widthInTiles, u16 heightInTiles, u32 tileCount, ptr tiles,
///     ptr metatiles, ptr plane[4], ptr palette}</c>. Tiles are 8x8 8bpp,
///     metatiles are four u16 tile references, and the raw palette contains 256
///     BGR555 colours. Map rows use u16-word offsets and the same top-two-bit
///     literal/repeat/copy commands as <see cref="GbaLaterLevelArt" />.
/// </remarks>
public static class GbaThps3LevelArt
{
    private const uint RomBase = 0x08000000;
    private const int TilePixels = 8;
    private const int TileBytes = 64;
    private const int MetatilePixels = 16;
    private const int MetatileBytes = 8;
    private const int PaletteColours = 256;
    private const int PaletteBytes = PaletteColours * 2;
    private const int MaxPlanes = 4;
    private const int MaxImageDimension = 8192;
    private const int ExpectedLevelCount = 9;

    /// <summary>Size of one THPS3 level-table record.</summary>
    public const int LevelRecordStride = 0x70;

    /// <summary>One THPS3 authored colour surface.</summary>
    public readonly record struct Thps3Level(
        int Index, int LevelRecordOffset, int PixelWidth, int PixelHeight);

    private readonly record struct Plane(int Width, int Height, ushort[] Metatiles);

    /// <summary>True when the ROM contains THPS3's nine-level colour table.</summary>
    public static bool IsThps3LevelRom(ReadOnlySpan<byte> rom) => FindLevels(rom).Count > 0;

    /// <summary>Finds the THPS3 level table without relying on a regional ROM offset.</summary>
    public static List<Thps3Level> FindLevels(ReadOnlySpan<byte> rom)
    {
        var result = new List<Thps3Level>();
        if (!HasThps3GameCode(rom))
            return result;

        var table = LocateTable(rom);
        if (table < 0)
            return result;

        for (var i = 0; i < ExpectedLevelCount; i++)
        {
            var record = table + i * LevelRecordStride;
            var tilesWide = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(record, 2));
            var tilesHigh = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(record + 2, 2));
            result.Add(new Thps3Level(i, record, tilesWide * TilePixels, tilesHigh * TilePixels));
        }

        return result;
    }

    /// <summary>Reconstructs the complete, painter-ordered level surface.</summary>
    public static GbaLevelImages.GbaLevelRender? RenderColourSurface(
        ReadOnlySpan<byte> rom, Thps3Level level)
    {
        var record = level.LevelRecordOffset;
        if (!LooksLikeRecord(rom, record))
            return null;

        var tileCount = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(record + 0x04, 4));
        var tilePool = PointerToOffset(rom, record + 0x08);
        var metatileTable = PointerToOffset(rom, record + 0x0C);
        var palette = TryGetPalette(rom, level);
        if (tilePool < 0 || metatileTable < 0 || palette == null)
            return null;

        var planes = new List<Plane>(MaxPlanes);
        var maxMetatile = -1;
        for (var i = 0; i < MaxPlanes; i++)
        {
            var pointerSite = record + 0x10 + i * 4;
            var address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(pointerSite, 4));
            if (address == 0)
                continue;
            var planeOffset = PointerToOffset(rom, pointerSite);
            var plane = ReadPlane(rom, planeOffset);
            if (plane == null)
                return null;
            foreach (var metatile in plane.Value.Metatiles)
                maxMetatile = Math.Max(maxMetatile, metatile);
            planes.Add(plane.Value);
        }

        if (planes.Count == 0 || maxMetatile < 0
            || (long)metatileTable + (long)(maxMetatile + 1) * MetatileBytes > rom.Length)
            return null;

        var width = level.PixelWidth;
        var height = level.PixelHeight;
        if (width is <= 0 or > MaxImageDimension || height is <= 0 or > MaxImageDimension)
            return null;
        var rgba = new byte[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            rgba[pixel * 4] = 0x12;
            rgba[pixel * 4 + 1] = 0x12;
            rgba[pixel * 4 + 2] = 0x16;
            rgba[pixel * 4 + 3] = 0xFF;
        }

        foreach (var plane in planes)
        {
            for (var y = 0; y < plane.Height; y++)
            for (var x = 0; x < plane.Width; x++)
            {
                var metatile = plane.Metatiles[y * plane.Width + x];
                if ((long)metatile * MetatileBytes + MetatileBytes
                    > (long)(maxMetatile + 1) * MetatileBytes)
                    return null;
                for (var quad = 0; quad < 4; quad++)
                {
                    var tileRef = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(
                        metatileTable + metatile * MetatileBytes + quad * 2, 2));
                    var tile = tileRef & 0x3FFF;
                    if (tile >= tileCount)
                        return null;
                    BlitTile(
                        rom, tilePool, (int)tile, (tileRef & 0x4000) != 0, (tileRef & 0x8000) != 0,
                        x * MetatilePixels + quad % 2 * TilePixels,
                        y * MetatilePixels + quad / 2 * TilePixels,
                        palette, rgba, width, height);
                }
            }
        }

        return new GbaLevelImages.GbaLevelRender(width, height, rgba);
    }

    /// <summary>Returns the source BGR555 palette converted to RGBA.</summary>
    public static byte[]? TryGetPalette(ReadOnlySpan<byte> rom, Thps3Level level)
    {
        var paletteOffset = PointerToOffset(rom, level.LevelRecordOffset + 0x20);
        if (paletteOffset < 0 || paletteOffset + PaletteBytes > rom.Length)
            return null;
        var rgba = new byte[PaletteColours * 4];
        for (var i = 0; i < PaletteColours; i++)
        {
            var colour = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(paletteOffset + i * 2, 2));
            rgba[i * 4] = Expand5(colour & 0x1F);
            rgba[i * 4 + 1] = Expand5(colour >> 5 & 0x1F);
            rgba[i * 4 + 2] = Expand5(colour >> 10 & 0x1F);
            rgba[i * 4 + 3] = 0xFF;
        }

        return rgba;
    }

    private static int LocateTable(ReadOnlySpan<byte> rom)
    {
        var last = rom.Length - ExpectedLevelCount * LevelRecordStride;
        for (var offset = 0; offset <= last; offset += 4)
        {
            if (!LooksLikeRecord(rom, offset))
                continue;
            var all = true;
            for (var i = 1; i < ExpectedLevelCount; i++)
            {
                if (LooksLikeRecord(rom, offset + i * LevelRecordStride))
                    continue;
                all = false;
                break;
            }

            if (all)
                return offset;
        }

        return -1;
    }

    private static bool LooksLikeRecord(ReadOnlySpan<byte> rom, int record)
    {
        if (record < 0 || record + LevelRecordStride > rom.Length)
            return false;
        var tilesWide = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(record, 2));
        var tilesHigh = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(record + 2, 2));
        if (tilesWide is < 32 or > MaxImageDimension / TilePixels
            || tilesHigh is < 32 or > MaxImageDimension / TilePixels)
            return false;
        var tileCount = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(record + 0x04, 4));
        if (tileCount is 0 or > 0x4000)
            return false;
        var tilePool = PointerToOffset(rom, record + 0x08);
        var metatiles = PointerToOffset(rom, record + 0x0C);
        var firstPlane = PointerToOffset(rom, record + 0x10);
        var palette = PointerToOffset(rom, record + 0x20);
        if (tilePool < 0 || metatiles < 0 || firstPlane < 0 || palette < 0
            || palette + PaletteBytes != tilePool
            || (long)tilePool + tileCount * TileBytes > rom.Length)
            return false;
        if (firstPlane + 4 > rom.Length)
            return false;
        var mapWidth = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(firstPlane, 2));
        var mapHeight = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(firstPlane + 2, 2));
        return mapWidth * MetatilePixels == tilesWide * TilePixels
               && mapHeight > 0 && mapHeight <= MaxImageDimension / MetatilePixels + 1;
    }

    private static Plane? ReadPlane(ReadOnlySpan<byte> rom, int offset)
    {
        if (offset < 0 || offset + 4 > rom.Length)
            return null;
        var width = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 2, 2));
        if (width == 0 || height == 0
            || width > MaxImageDimension / MetatilePixels + 1
            || height > MaxImageDimension / MetatilePixels + 1)
            return null;
        var streamBaseLong = (long)offset + 4 + (long)height * 4;
        if (streamBaseLong > rom.Length || (long)width * height > int.MaxValue)
            return null;
        var values = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            var rowWords = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset + 4 + y * 4, 4));
            var cursorLong = streamBaseLong + rowWords * 2L;
            if (cursorLong < streamBaseLong || cursorLong + 2 > rom.Length)
                return null;
            var cursor = (int)cursorLong;
            var x = 0;
            while (x < width)
            {
                if (cursor + 2 > rom.Length)
                    return null;
                var command = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(cursor, 2));
                cursor += 2;
                var kind = command >> 14;
                if (kind < 2)
                {
                    values[y * width + x++] = command;
                    continue;
                }

                var count = command & 0x3FFF;
                if (count == 0 || count > width - x)
                    return null;
                if (kind == 2)
                {
                    if (cursor + 2 > rom.Length)
                        return null;
                    var value = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(cursor, 2));
                    cursor += 2;
                    values.AsSpan(y * width + x, count).Fill(value);
                }
                else
                {
                    var bytes = count * 2;
                    if (cursor + bytes > rom.Length)
                        return null;
                    for (var i = 0; i < count; i++)
                        values[y * width + x + i] = BinaryPrimitives.ReadUInt16LittleEndian(
                            rom.Slice(cursor + i * 2, 2));
                    cursor += bytes;
                }

                x += count;
            }
        }

        return new Plane(width, height, values);
    }

    private static void BlitTile(
        ReadOnlySpan<byte> rom, int tilePool, int tile, bool flipX, bool flipY,
        int left, int top, byte[] palette, byte[] rgba, int width, int height)
    {
        var tileOffset = tilePool + tile * TileBytes;
        for (var y = 0; y < TilePixels; y++)
        {
            var destY = top + y;
            if (destY < 0 || destY >= height)
                continue;
            var sourceY = flipY ? TilePixels - 1 - y : y;
            for (var x = 0; x < TilePixels; x++)
            {
                var destX = left + x;
                if (destX < 0 || destX >= width)
                    continue;
                var sourceX = flipX ? TilePixels - 1 - x : x;
                var paletteIndex = rom[tileOffset + sourceY * TilePixels + sourceX];
                if (paletteIndex == 0)
                    continue;
                var dest = (destY * width + destX) * 4;
                rgba[dest] = palette[paletteIndex * 4];
                rgba[dest + 1] = palette[paletteIndex * 4 + 1];
                rgba[dest + 2] = palette[paletteIndex * 4 + 2];
                rgba[dest + 3] = 0xFF;
            }
        }
    }

    private static int PointerToOffset(ReadOnlySpan<byte> rom, int pointerSite)
    {
        if (pointerSite < 0 || pointerSite + 4 > rom.Length)
            return -1;
        var address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(pointerSite, 4));
        return address >= RomBase && address < RomBase + (uint)rom.Length
            ? (int)(address - RomBase)
            : -1;
    }

    private static bool HasThps3GameCode(ReadOnlySpan<byte> rom) =>
        rom.Length > 0xAF && rom[0xAC] == (byte)'A' && rom[0xAD] == (byte)'T' && rom[0xAE] == (byte)'3';

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));
}
