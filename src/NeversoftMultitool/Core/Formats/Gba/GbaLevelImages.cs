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
    // record 0x144 bytes earlier — its true base is 0x087533FC, stride 0x15C. Fields,
    // as deltas from the scanned base (= true base + 0x144):
    //   true +0x24/+0x26 colour-plane dims (2× tile width/height)   -> -0x120/-0x11E
    //   true +0x2C tile pool (raw 8bpp 64-byte tiles, index 0 transparent) -> -0x118
    //   true +0x34 plane 0 tilemap (floor/wall, behind)             -> -0x110
    //   true +0x38 plane 1 tilemap (ramps/detail, front)            -> -0x10C
    //   true +0x3C BG palette (LZ77 -> 256 BGR555)                  -> -0x108
    //   true +0x13C/+0x140/+0x144 collision heightfield (see RenderIsoHeightfield)
    private const int PaletteFieldDelta = -0x108;
    private const int ColourDimsDelta = -0x120;
    private const int TilePoolPtrDelta = -0x118;
    private const int Plane0PtrDelta = -0x110;
    private const int Plane1PtrDelta = -0x10C;
    private const int PaletteColors = 256;
    private const int TileBytes = 64; // 8×8 8bpp

    public readonly record struct GbaLevel(
        uint RecordAddress, uint ObjectListAddress, uint ElementLibraryAddress, int ElementCount);

    /// <summary>A rendered level: <paramref name="Coverage" /> is one byte per pixel, 0 or 1.</summary>
    public readonly record struct GbaLevelBitmap(int Width, int Height, byte[] Coverage);

    /// <summary>An RGBA render of a level (row-major, 4 bytes/pixel).</summary>
    public readonly record struct GbaLevelRender(int Width, int Height, byte[] Rgba);

    // Isometric heightfield-render constants (from the engine: cell = 3 world units,
    // the render matches the loader's iso basis).
    private const int IsoTileW = 18;
    private const int IsoTileH = 9;
    private const double IsoZScale = IsoTileW / 3.0;
    private const int TrueRecordDelta = -0x144; // scan record → loader's true record base
    private const int DimsField = 0x13C;        // then +0x13E = height
    private const int ColourMapField = 0x140;
    private const int CellRecTableField = 0x144;
    private const int CellRecStride = 32;

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
            {
                for (var c = 0; c < o.GridWidth; c++)
                {
                    int v = BinaryPrimitives.ReadInt16LittleEndian(
                        rom.Slice(gridOffset + (r * o.GridWidth + c) * 2, 2));
                    if (v <= 0 || v >= elementCount)
                        continue;
                    BlitElement(coverage, width, height, elements, v, originX + c * TileSize, originY + r * TileSize);
                }
            }
        }

        return new GbaLevelBitmap(width, height, coverage);
    }

    /// <summary>
    ///     Renders the level's terrain as an isometric heightfield — the accurate 3D
    ///     structure the engine draws. Media-derived: the per-cell height / shape /
    ///     material grid at the record's <c>+0x13C/+0x140/+0x144</c> fields (this is the
    ///     collision-terrain heightfield, which mirrors the visual surface). Surfaces
    ///     are shaded by height and tinted per material <b>for structure visibility</b>;
    ///     the engine's exact per-pixel surface colour is produced by a separate pixel
    ///     renderer not yet reverse-engineered, so these are representative colours, not
    ///     the game's. The level's real palette is available via <see cref="TryGetPalette" />.
    /// </summary>
    public static GbaLevelRender? RenderIsoHeightfield(ReadOnlySpan<byte> rom, GbaLevel level)
    {
        var trueRecord = (int)(level.RecordAddress - RomBase) + TrueRecordDelta;
        if (trueRecord < 0 || trueRecord + CellRecTableField + 4 > rom.Length)
            return null;

        int w = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(trueRecord + DimsField, 2));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(trueRecord + DimsField + 2, 2));
        var colourMap = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(trueRecord + ColourMapField, 4));
        var cellRecTable = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(trueRecord + CellRecTableField, 4));
        if (w is <= 0 or > 256 || h is <= 0 or > 256 || !IsRomPointer(colourMap) || !IsRomPointer(cellRecTable))
            return null;

        var heights = new double[h, w];
        var materials = new int[h, w];
        var mapOffset = (int)(colourMap - RomBase);
        var tableOffset = (int)(cellRecTable - RomBase);
        if (mapOffset < 0 || mapOffset + w * h * 2 > rom.Length)
            return null;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var mapValue = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(mapOffset + (y * w + x) * 2, 2));
                var rec = tableOffset + mapValue * CellRecStride;
                if (rec < 0 || rec + CellRecStride > rom.Length)
                    continue;
                materials[y, x] = rom[rec + 2];
                heights[y, x] = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(rec + 8, 2)) / 4096.0;
            }
        }

        // Project every cell corner (top at its height, base at 0) to size the canvas.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var y = 0; y <= h; y++)
        {
            for (var x = 0; x <= w; x++)
            {
                var top = y < h && x < w ? heights[y, x] : 0;
                foreach (var z in (ReadOnlySpan<double>)[top, 0.0])
                {
                    var px = (x - y) * (IsoTileW / 2.0);
                    var py = (x + y) * (IsoTileH / 2.0) - z * IsoZScale;
                    minX = Math.Min(minX, px);
                    maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py);
                    maxY = Math.Max(maxY, py);
                }
            }
        }

        var width = (int)(maxX - minX) + 8;
        var height = (int)(maxY - minY) + 8;
        if (width is <= 0 or > MaxImageDimension || height is <= 0 or > MaxImageDimension)
            return null;
        var originX = -minX + 4;
        var originY = -minY + 4;

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = 0x12;
            rgba[i * 4 + 1] = 0x12;
            rgba[i * 4 + 2] = 0x16;
            rgba[i * 4 + 3] = 0xFF;
        }

        (double X, double Y) Project(double gx, double gy, double gz) =>
            (originX + (gx - gy) * (IsoTileW / 2.0), originY + (gx + gy) * (IsoTileH / 2.0) - gz * IsoZScale);

        // Painter's order: back-to-front along the gx+gy diagonal.
        for (var s = 0; s <= w + h - 2; s++)
        {
            for (var y = 0; y < h; y++)
            {
                RenderCell(rgba, width, height, heights, materials, w, s, y, Project);
            }
        }

        return new GbaLevelRender(width, height, rgba);
    }

    private static void RenderCell(
        byte[] rgba, int width, int height, double[,] heights, int[,] materials, int w, int s, int y,
        Func<double, double, double, (double X, double Y)> project)
    {
        var x = s - y;
        if (x < 0 || x >= w)
            return;
        var hh = heights[y, x];
        var (r, g, b) = MaterialColor(materials[y, x], hh);
        var top0 = project(x, y, hh);
        var top1 = project(x + 1, y, hh);
        var top2 = project(x + 1, y + 1, hh);
        var top3 = project(x, y + 1, hh);
        if (hh > 0.05)
        {
            // Two visible side faces down to the ground, darkened for shading.
            FillQuad(rgba, width, height, [top3, top2, project(x + 1, y + 1, 0), project(x, y + 1, 0)],
                (byte)(r * 0.5), (byte)(g * 0.5), (byte)(b * 0.5));
            FillQuad(rgba, width, height, [top1, top2, project(x + 1, y + 1, 0), project(x + 1, y, 0)],
                (byte)(r * 0.7), (byte)(g * 0.7), (byte)(b * 0.7));
        }

        FillQuad(rgba, width, height, [top0, top1, top2, top3], r, g, b);
    }

    // Per-material visualization tint (distinct hue), brightened by height so the 3D
    // structure reads. NOT the engine's colour — the exact shader is not yet RE'd.
    private static (byte R, byte G, byte B) MaterialColor(int material, double height)
    {
        var hue = (material * 0.16 + 0.05) % 1.0;
        var (r, g, b) = HsvToRgb(hue, 0.5, 1.0);
        var brightness = 0.6 + 0.35 * Math.Min(height / 15.0, 1.0);
        return ((byte)(r * 255 * brightness), (byte)(g * 255 * brightness), (byte)(b * 255 * brightness));
    }

    private static (double R, double G, double B) HsvToRgb(double hue, double sat, double val)
    {
        var i = (int)(hue * 6) % 6;
        var f = hue * 6 - Math.Floor(hue * 6);
        var p = val * (1 - sat);
        var q = val * (1 - f * sat);
        var t = val * (1 - (1 - f) * sat);
        return i switch
        {
            0 => (val, t, p),
            1 => (q, val, p),
            2 => (p, val, t),
            3 => (p, q, val),
            4 => (t, p, val),
            _ => (val, p, q)
        };
    }

    // Scanline fill of a convex quad into the RGBA buffer (painter's order, no z-buffer).
    private static void FillQuad(
        byte[] rgba, int width, int height, ReadOnlySpan<(double X, double Y)> pts, byte r, byte g, byte b)
    {
        double yMin = double.MaxValue, yMax = double.MinValue;
        foreach (var p in pts)
        {
            yMin = Math.Min(yMin, p.Y);
            yMax = Math.Max(yMax, p.Y);
        }

        var y0 = Math.Max(0, (int)Math.Ceiling(yMin));
        var y1 = Math.Min(height - 1, (int)Math.Floor(yMax));
        Span<double> xs = stackalloc double[8];
        for (var y = y0; y <= y1; y++)
        {
            var scanY = y + 0.5;
            var count = 0;
            for (var e = 0; e < pts.Length; e++)
            {
                var a = pts[e];
                var c = pts[(e + 1) % pts.Length];
                if (a.Y <= scanY && c.Y > scanY || c.Y <= scanY && a.Y > scanY)
                    xs[count++] = a.X + (scanY - a.Y) / (c.Y - a.Y) * (c.X - a.X);
            }

            if (count < 2)
                continue;
            xs[..count].Sort();
            for (var i = 0; i + 1 < count; i += 2)
            {
                var xStart = Math.Max(0, (int)Math.Ceiling(xs[i] - 0.5));
                var xEnd = Math.Min(width - 1, (int)Math.Floor(xs[i + 1] - 0.5));
                for (var x = xStart; x <= xEnd; x++)
                {
                    var o = (y * width + x) * 4;
                    rgba[o] = r;
                    rgba[o + 1] = g;
                    rgba[o + 2] = b;
                    rgba[o + 3] = 0xFF;
                }
            }
        }
    }

    /// <summary>
    ///     Renders the level's <b>true full-colour isometric surface</b> — the actual
    ///     game appearance. The engine draws the level as GBA Mode-2 affine backgrounds
    ///     whose art is pre-baked isometric 8-bit tile graphics in ROM (there is no
    ///     software rasterizer and no CPU framebuffer write — the tiles are DMA'd and
    ///     hardware-affine-transformed). This composites that art directly from media:
    ///     two tilemaps (record <c>+0x34</c> floor/wall behind, <c>+0x38</c> ramps in
    ///     front) index an 8bpp tile pool (<c>+0x2C</c>, 64-byte tiles, index 0
    ///     transparent) coloured through the palette (<c>+0x3C</c>); the tile
    ///     width/height are stored at <c>+0x24/+0x26</c> as twice the tile count.
    ///     Validated against the emulator: every tile is byte-exact and the frame
    ///     quantises to this palette at 1.03/255.
    /// </summary>
    public static GbaLevelRender? RenderColourSurface(ReadOnlySpan<byte> rom, GbaLevel level)
    {
        var palette = TryGetPalette(rom, level);
        if (palette is null)
            return null;

        var rec = (int)(level.RecordAddress - RomBase);
        if (rec + ColourDimsDelta < 0 || rec + PaletteFieldDelta + 4 > rom.Length)
            return null;

        var width2 = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(rec + ColourDimsDelta, 2));
        var height2 = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(rec + ColourDimsDelta + 2, 2));
        if (width2 == 0 || height2 == 0 || width2 % 2 != 0 || height2 % 2 != 0)
            return null;
        var tilesWide = width2 / 2;
        var tilesHigh = height2 / 2;
        var imageW = tilesWide * 8;
        var imageH = tilesHigh * 8;
        if (imageW is <= 0 or > MaxImageDimension || imageH is <= 0 or > MaxImageDimension)
            return null;

        var poolAddress = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(rec + TilePoolPtrDelta, 4));
        if (!IsRomPointer(poolAddress))
            return null;
        var poolOffset = (int)(poolAddress - RomBase);

        var rgba = new byte[imageW * imageH * 4];
        for (var i = 0; i < imageW * imageH; i++)
        {
            rgba[i * 4] = 0x12;
            rgba[i * 4 + 1] = 0x12;
            rgba[i * 4 + 2] = 0x16;
            rgba[i * 4 + 3] = 0xFF;
        }

        // Draw the detail plane (+0x38) first, then the main surface plane (+0x34)
        // in front — the main surface (floor / corrugated wall) occludes the detail
        // plane except through its own transparent cells, matching the hardware BG order.
        foreach (var planeDelta in (ReadOnlySpan<int>)[Plane1PtrDelta, Plane0PtrDelta])
        {
            var planeAddress = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(rec + planeDelta, 4));
            if (!IsRomPointer(planeAddress))
                continue;
            if (!GbaBiosLz77.TryDecompress(rom, (int)(planeAddress - RomBase), out var map, out _))
                continue;
            if (map.Length != tilesWide * tilesHigh * 2)
                continue;

            for (var cell = 0; cell < tilesWide * tilesHigh; cell++)
            {
                var tileIndex = BinaryPrimitives.ReadUInt16LittleEndian(map.AsSpan(cell * 2, 2));
                if (tileIndex == 0) // whole-tile transparent
                    continue;
                var tileOffset = poolOffset + tileIndex * TileBytes;
                if (tileOffset < 0 || tileOffset + TileBytes > rom.Length)
                    continue;
                BlitTile(rgba, imageW, rom, tileOffset, palette, (cell % tilesWide) * 8, (cell / tilesWide) * 8);
            }
        }

        return new GbaLevelRender(imageW, imageH, rgba);
    }

    private static void BlitTile(
        byte[] rgba, int imageW, ReadOnlySpan<byte> rom, int tileOffset, byte[] palette, int px, int py)
    {
        for (var r = 0; r < 8; r++)
        {
            for (var c = 0; c < 8; c++)
            {
                var index = rom[tileOffset + r * 8 + c];
                if (index == 0) // palette index 0 is the transparent key
                    continue;
                var o = ((py + r) * imageW + px + c) * 4;
                rgba[o] = palette[index * 4];
                rgba[o + 1] = palette[index * 4 + 1];
                rgba[o + 2] = palette[index * 4 + 2];
                rgba[o + 3] = 0xFF;
            }
        }
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
