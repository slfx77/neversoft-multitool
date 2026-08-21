using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Reconstructs the <b>isometric level images</b> the Vicarious Visions GBA Tony
///     Hawk engine draws — the "render a level" deliverable, in 2D, since the engine
///     has no polygon meshes (levels are composited from tile bitmaps by a software
///     rasterizer). Everything derives from the ROM; no emulator capture is needed.
///
///     <para><b>Level table.</b> A table of records (stride 0x15C) holds, per level,
///     <c>{ shared@+0, objectListPtr@+4, elementLibraryPtr@+8, meta@+0xC }</c>. The
///     table is located by content — a run of records whose object-list and
///     element-library fields are both in-ROM and whose element library begins with
///     a BIOS-LZ77 header (0x10) that decompresses to a whole number of 96-byte
///     elements. Several records can share a level's assets (THPS2's Hangar has five
///     mode variants), so <see cref="FindLevels" /> returns the distinct
///     object-list/element-library pairs.</para>
///
///     <para><b>Elements.</b> The element library is a BIOS-LZ77 stream
///     (<see cref="GbaBiosLz77" />) decompressing to N × 96 bytes; each 96-byte
///     element is a 24×24 1-bit bitmap (one u32 per row, pixel column c = bit c,
///     LSB first — established by simulating the blitter at ROM 0x087FE068). Element
///     0 is the empty tile.</para>
///
///     <para><b>Objects.</b> The object list is 96-byte records
///     <c>{ s32 bbox[minX,minY,maxX,maxY]@0, u32 gridPtr@0x10, u32 gridWidth@0x14 }</c>
///     ending at a zero record. Each object owns a <c>gridWidth × ceil(height/24)</c>
///     grid of s16 tile indices; a cell's value V (≠0) blits element V at pixel
///     <c>(minX + col*24, minY + row*24)</c>, painter's order.</para>
///
///     The blitter writes a single ink value where a bit is set, so the output is
///     2-tone (dither tiles read as shades). Colour is a separate, not-yet-RE'd fill
///     pass, so images are rendered as ink coverage. Verified against THPS2 (GBA):
///     the reconstruction is a recognizable Hangar level (rails, helicopter, halfpipe).
/// </summary>
public static class GbaLevelImages
{
    private const uint RomBase = 0x08000000;
    private const uint RomEnd = 0x0A000000;
    private const int TileSize = 24;
    private const int ElementBytes = 96;        // 24 rows × u32
    private const int LevelRecordStride = 0x15C;
    private const int ObjectStride = 0x60;
    private const int MaxImageDimension = 8192; // guards a pathological bbox

    // The record is located by content-scanning for the {objectList, elementLibrary}
    // pointer pair (at scan-relative +4/+8). The engine's loader addresses the same
    // record 0x144 bytes earlier — its true base is 0x087533FC, stride 0x15C, with
    // palette@+0x3C, dims@+0x13C/+0x13E, colourMap@+0x140, cellRecs@+0x144,
    // objectList@+0x148, elementLibrary@+0x14C. So from the scanned base the
    // per-level BG palette pointer sits at -0x108 (= true +0x3C).
    private const int PaletteFieldDelta = -0x108;
    private const int PaletteColors = 256;

    public readonly record struct GbaLevel(
        uint RecordAddress, uint ObjectListAddress, uint ElementLibraryAddress, int ElementCount);

    /// <summary>A rendered level: <paramref name="Coverage" /> is one byte per pixel, 0 or 1.</summary>
    public readonly record struct GbaLevelBitmap(int Width, int Height, byte[] Coverage);

    /// <summary>The distinct isometric levels in the ROM (deduplicated by asset pointers).</summary>
    public static List<GbaLevel> FindLevels(ReadOnlySpan<byte> rom)
    {
        var levels = new List<GbaLevel>();
        var tableStart = LocateTable(rom);
        if (tableStart < 0)
            return levels;

        var seen = new HashSet<(uint, uint)>();
        for (var rec = tableStart; rec + 0x10 <= rom.Length; rec += LevelRecordStride)
        {
            var objectList = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(rec + 4, 4));
            var elementLib = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(rec + 8, 4));
            if (!IsRomPointer(objectList) || !TryElementCount(rom, elementLib, out var elementCount))
                break; // the table ends at the first record that doesn't validate

            if (seen.Add((objectList, elementLib)))
                levels.Add(new GbaLevel(
                    RomBase + (uint)rec, objectList, elementLib, elementCount));
        }

        return levels;
    }

    /// <summary>Composites one level's tiles into a coverage bitmap, or null if it can't be read.</summary>
    public static GbaLevelBitmap? RenderLevel(ReadOnlySpan<byte> rom, GbaLevel level)
    {
        if (!GbaBiosLz77.TryDecompress(rom, (int)(level.ElementLibraryAddress - RomBase), out var elements, out _))
            return null;
        var elementCount = elements.Length / ElementBytes;
        var objects = ReadObjects(rom, level.ObjectListAddress);
        if (objects.Count == 0)
            return null;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var o in objects)
        {
            minX = Math.Min(minX, o.MinX);
            minY = Math.Min(minY, o.MinY);
            maxX = Math.Max(maxX, o.MaxX);
            maxY = Math.Max(maxY, o.MaxY);
        }

        var width = maxX - minX + TileSize + 8;
        var height = maxY - minY + TileSize + 8;
        if (width is <= 0 or > MaxImageDimension || height is <= 0 or > MaxImageDimension)
            return null;

        var coverage = new byte[width * height];
        foreach (var o in objects)
        {
            var originX = o.MinX - minX;
            var originY = o.MinY - minY;
            var rows = Math.Max(1, (o.MaxY - o.MinY + TileSize - 1) / TileSize);
            var cellCount = o.GridWidth * rows;
            var gridOffset = (int)(o.GridPtr - RomBase);
            if (gridOffset < 0 || gridOffset + cellCount * 2 > rom.Length)
                continue;

            for (var r = 0; r < rows; r++)
            for (var c = 0; c < o.GridWidth; c++)
            {
                int v = BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(gridOffset + (r * o.GridWidth + c) * 2, 2));
                if (v <= 0 || v >= elementCount)
                    continue;
                BlitElement(coverage, width, height, elements, v, originX + c * TileSize, originY + r * TileSize);
            }
        }

        return new GbaLevelBitmap(width, height, coverage);
    }

    /// <summary>
    ///     The level's real 256-colour background palette (BGR555 → RGBA), or null.
    ///     This is the actual colour source — re-quantising a screenshot to it is
    ///     byte-exact — but the engine paints each surface with a per-material
    ///     procedural shader that indexes this palette by height/slope/light, so the
    ///     palette alone does not give a pixel-faithful colour render (that needs the
    ///     37 material shaders reimplemented). Exposed as the level's colour asset.
    /// </summary>
    public static byte[]? TryGetPalette(ReadOnlySpan<byte> rom, GbaLevel level)
    {
        var pointerOffset = (int)(level.RecordAddress - RomBase) + PaletteFieldDelta;
        if (pointerOffset < 0 || pointerOffset + 4 > rom.Length)
            return null;
        var paletteAddress = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(pointerOffset, 4));
        if (!IsRomPointer(paletteAddress))
            return null;
        if (!GbaBiosLz77.TryDecompress(rom, (int)(paletteAddress - RomBase), out var raw, out _))
            return null;
        if (raw.Length != PaletteColors * 2)
            return null;

        var rgba = new byte[PaletteColors * 4];
        for (var i = 0; i < PaletteColors; i++)
        {
            var c = raw[i * 2] | (raw[i * 2 + 1] << 8);
            rgba[i * 4] = Expand5(c & 0x1F);
            rgba[i * 4 + 1] = Expand5((c >> 5) & 0x1F);
            rgba[i * 4 + 2] = Expand5((c >> 10) & 0x1F);
            rgba[i * 4 + 3] = 0xFF;
        }

        return rgba;
    }

    /// <summary>Renders a coverage bitmap to RGBA — ink (set bits) dark on a light ground.</summary>
    public static byte[] ToRgba(GbaLevelBitmap bitmap)
    {
        var rgba = new byte[bitmap.Width * bitmap.Height * 4];
        for (var i = 0; i < bitmap.Coverage.Length; i++)
        {
            var ink = bitmap.Coverage[i] != 0;
            var shade = (byte)(ink ? 0x28 : 0xF0);
            rgba[i * 4] = shade;
            rgba[i * 4 + 1] = shade;
            rgba[i * 4 + 2] = shade;
            rgba[i * 4 + 3] = 0xFF;
        }

        return rgba;
    }

    private readonly record struct GbaObject(int MinX, int MinY, int MaxX, int MaxY, uint GridPtr, int GridWidth);

    private static List<GbaObject> ReadObjects(ReadOnlySpan<byte> rom, uint objectListAddress)
    {
        var objects = new List<GbaObject>();
        var off = (int)(objectListAddress - RomBase);
        while (off >= 0 && off + ObjectStride <= rom.Length)
        {
            var minX = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(off, 4));
            var minY = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(off + 4, 4));
            var maxX = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(off + 8, 4));
            var maxY = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(off + 12, 4));
            var gridPtr = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(off + 0x10, 4));
            var gridWidth = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(off + 0x14, 4));

            // The list ends at the first record that isn't a valid object (a zero
            // terminator, or a degenerate/out-of-ROM record).
            if (!IsRomPointer(gridPtr) || maxX <= minX || maxY <= minY || gridWidth is <= 0 or > 4096)
                break;

            objects.Add(new GbaObject(minX, minY, maxX, maxY, gridPtr, gridWidth));
            off += ObjectStride;
        }

        return objects;
    }

    private static void BlitElement(
        byte[] coverage, int width, int height, byte[] elements, int element, int px, int py)
    {
        var baseOffset = element * ElementBytes;
        for (var ry = 0; ry < TileSize; ry++)
        {
            var yy = py + ry;
            if (yy < 0 || yy >= height)
                continue;
            var word = BinaryPrimitives.ReadUInt32LittleEndian(elements.AsSpan(baseOffset + ry * 4, 4));
            if (word == 0)
                continue;
            var rowBase = yy * width;
            for (var cx = 0; cx < TileSize; cx++)
            {
                if (((word >> cx) & 1) == 0)
                    continue;
                var xx = px + cx;
                if (xx >= 0 && xx < width)
                    coverage[rowBase + xx] = 1;
            }
        }
    }

    // The first 4-aligned offset where four consecutive stride-0x15C records all
    // fully validate: an object list that opens on a real object, and an element
    // library that decompresses to a whole number of 96-byte tiles. The cheap
    // pointer/header checks gate the expensive decompress so the whole-ROM scan
    // only decompresses at the handful of offsets where four pointer pairs align.
    private static int LocateTable(ReadOnlySpan<byte> rom)
    {
        for (var off = 0; off + 4 * LevelRecordStride <= rom.Length; off += 4)
        {
            var ok = true;
            for (var k = 0; k < 4 && ok; k++)
                ok = IsLevelRecord(rom, off + k * LevelRecordStride);
            if (ok)
                return off;
        }

        return -1;
    }

    private static bool IsLevelRecord(ReadOnlySpan<byte> rom, int rec)
    {
        var objectList = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(rec + 4, 4));
        var elementLib = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(rec + 8, 4));
        var elemOffset = (int)(elementLib - RomBase);
        if (!IsRomPointer(objectList) || !IsRomPointer(elementLib)
            || elemOffset >= rom.Length || rom[elemOffset] != 0x10)
            return false;

        // The object list must open on a real object (valid bbox + in-ROM grid).
        var objOffset = (int)(objectList - RomBase);
        if (objOffset < 0 || objOffset + ObjectStride > rom.Length)
            return false;
        var minX = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(objOffset, 4));
        var minY = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(objOffset + 4, 4));
        var maxX = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(objOffset + 8, 4));
        var maxY = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(objOffset + 12, 4));
        var gridPtr = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(objOffset + 0x10, 4));
        if (maxX <= minX || maxY <= minY || !IsRomPointer(gridPtr))
            return false;

        return TryElementCount(rom, elementLib, out _);
    }

    private static bool TryElementCount(ReadOnlySpan<byte> rom, uint elementLib, out int count)
    {
        count = 0;
        if (!IsRomPointer(elementLib))
            return false;
        if (!GbaBiosLz77.TryDecompress(rom, (int)(elementLib - RomBase), out var payload, out _))
            return false;
        if (payload.Length == 0 || payload.Length % ElementBytes != 0)
            return false;
        count = payload.Length / ElementBytes;
        return true;
    }

    private static bool IsRomPointer(uint address) => address is >= RomBase and < RomEnd;

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
}
