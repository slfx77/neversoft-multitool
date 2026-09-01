using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     The isometric level art of the four later Vicarious Visions GBA Tony Hawk
///     cartridges — THPS4, THUG, THUG2 and American Sk8land. Their parent level
///     records contain the full-colour tile surface as well as a separate occlusion
///     mask used to dither a skater hidden behind foreground scenery.
///
///     <para><b>Colour surface.</b> The parent record begins with its dimensions in
///     8x8 tiles. It then carries <c>{totalTileCount, fourBppTileCount}@+0x04</c>,
///     a mixed 4bpp/8bpp tile pool at <c>+0x08</c>, a raw 2x2 metatile table at
///     <c>+0x0C</c>, up to four row-RLE metatile maps at <c>+0x10..+0x1C</c>, and a
///     raw 256-colour BGR555 palette at <c>+0x20</c>. A 4bpp tile has a per-tile
///     remap table that maps its local 16 colours into the shared 256-colour
///     palette. Its packed rows are stored right-to-left: the engine expands byte
///     3 through byte 0, high nibble then low nibble. The remaining tiles use
///     direct 8bpp palette indices. Metatile tile
///     references use their low 14 bits as the tile index and bits 14/15 as the
///     horizontal/vertical flip flags.</para>
///
///     <para><b>Map compression.</b> Each map starts with
///     <c>{u16 width, u16 height, u32 rowOffsets[height]}</c>. Row offsets are in
///     u16 words from the stream immediately following that table. A command with
///     top bits 0 or 1 is one literal; top bits 2 repeat the following u16
///     <c>command&amp;0x3FFF</c> times; top bits 3 copy that many following literals.
///     The planes composite in pointer order, palette index zero transparent.</para>
///
///     <para><b>Occlusion mask.</b> The separate 0x1C-stride table entry is
///     <c>{ptr map, ptr objects, ptr grids, ptr metatiles, ptr elements, ptr ?, u32 flags}</c>.
///     Fields are BIOS-LZ77 streams or raw bytes; the engine chooses per field from
///     the flags word's own bytes — <c>+0x18</c> for the map, <c>+0x19</c> for the
///     objects, <c>+0x1A</c> for the metatiles — while the grids and elements are
///     always compressed (transcribed from the loader at THUG2 ROM 0x0803FBC8, which
///     also sizes the object table by dividing by 12).</para>
///
///     An element is 32 bytes: 16 rows of a
///     little-endian u16 whose bit <c>c</c> is pixel column <c>c</c>, so a 16x16
///     stamp. Four elements make a 32x32 metatile (2x2, row-major), and one u16 of a
///     grid selects a metatile. This one-bit payload is not the visible level art:
///     it is the foreground coverage mask used by the engine's occlusion pass.
///
///     <para><b>Placement.</b> The parent level record states the level's pixel size
///     as a u16 pair scaled by 8, and the map is that size in <b>64-pixel cells</b>
///     (the loader computes the width as <c>(pixelWidth + 32) / 64</c>). The map is
///     an array of u16 <b>run starts</b>: its non-empty entries are strictly
///     increasing from 0 to <c>objectCount - 1</c>, so cell <c>c</c> owns objects
///     <c>map[c]</c> up to the next non-empty cell's value, which partitions the
///     object table exactly. <c>0xFFFF</c> marks an empty cell.</para>
///
/// </summary>
public static class GbaLaterLevelArt
{
    private const uint RomBase = 0x08000000;

    /// <summary>The carved occlusion table's record stride.</summary>
    public const int ArtRecordStride = 0x1C;

    private const int ElementBytes = 32;   // 16 rows x u16
    private const int MetatileBytes = 8;   // 2x2 element indices
    private const int CellPixels = 64;     // one map cell
    private const int MetatilePixels = 32;
    private const int ElementPixels = 16;
    private const int ObjectRecordBytes = 12;
    private const int EmptyCell = 0xFFFF;
    private const int MinTableRecords = 3;
    private const int MaxImageDimension = 8192;
    private const int ColourTilePixels = 8;
    private const int ColourMetatilePixels = 16;
    private const int FourBppTileBytes = 32;
    private const int EightBppTileBytes = 64;
    private const int ColourMetatileBytes = 8;
    private const int PaletteColours = 256;
    private const int PaletteBytes = PaletteColours * 2;
    private const int MaxColourPlanes = 4;

    /// <summary>
    ///     One level: its occlusion record, the geometry its level record states, and which
    ///     flags byte (if any) gates the grid pool's compression on this cartridge.
    /// </summary>
    /// <remarks>
    ///     <paramref name="GridFlagOffset" /> is <see cref="AlwaysCompressed" /> on
    ///     THPS4/THUG/THUG2, whose loaders decompress the grid pool unconditionally,
    ///     and <c>0x1B</c> on American Sk8land, which gates it like the other fields.
    ///     The choice is measured per ROM rather than tabled per game.
    /// </remarks>
    public readonly record struct LaterLevel(
        int Index, int ArtRecordOffset, int LevelRecordOffset,
        int PixelWidth, int PixelHeight, int MapWidth, int MapHeight, int GridFlagOffset);

    /// <summary>A field with no flags byte: the engine always decompresses it.</summary>
    public const int AlwaysCompressed = -1;

    /// <summary>The five decoded occlusion payloads of one level.</summary>
    private sealed record OcclusionPayloads(
        byte[] Map, byte[] Objects, byte[] Grids, byte[] Metatiles, byte[] Elements);

    private readonly record struct ColourPlane(int Width, int Height, ushort[] Metatiles);

    private sealed record ColourPayloads(
        int TileCount,
        int FourBppTileCount,
        int TilePoolOffset,
        int PaletteIdsOffset,
        int PaletteRemapsOffset,
        int MetatileTableOffset,
        int MetatileCount,
        byte[] Palette,
        List<ColourPlane> Planes);

    /// <summary>True when this ROM carries a later-cart art table.</summary>
    public static bool IsLaterLevelRom(ReadOnlySpan<byte> rom) => FindLevels(rom).Count > 0;

    /// <summary>Every level this ROM's art table describes, in table order.</summary>
    public static List<LaterLevel> FindLevels(ReadOnlySpan<byte> rom)
    {
        var levels = new List<LaterLevel>();
        var table = LocateTable(rom, out var count);
        if (table < 0)
            return levels;

        // The offset of the art pointer inside the parent level record is constant
        // per cartridge but differs between them, so it is measured once from the
        // records whose map is compressed (there, the map's own cell count referees
        // the candidate) and then applied to the whole table.
        var pointerDelta = MeasurePointerDelta(rom, table, count);
        if (pointerDelta < 0)
            return levels;

        var gridFlag = MeasureGridFlag(rom, table, count);
        if (gridFlag == int.MinValue)
            return levels;

        for (var i = 0; i < count; i++)
        {
            var art = table + i * ArtRecordStride;
            var site = FindPointerSite(rom, RomBase + (uint)art);
            if (site < 0 || site - pointerDelta < 0)
                continue;
            var record = site - pointerDelta;
            var (w, h) = ReadPixelSize(rom, record);
            var mapW = MapCells(w);
            var mapH = MapCells(h);
            if (mapW <= 0 || mapH <= 0)
                continue;
            if ((long)mapW * CellPixels > MaxImageDimension || (long)mapH * CellPixels > MaxImageDimension)
                continue;
            levels.Add(new LaterLevel(i, art, record, w, h, mapW, mapH, gridFlag));
        }

        return levels;
    }

    /// <summary>
    ///     Composites the full-colour tile surface the game displays, or null when
    ///     the parent record or one of its referenced assets is malformed.
    /// </summary>
    public static GbaLevelImages.GbaLevelRender? RenderColourSurface(
        ReadOnlySpan<byte> rom, LaterLevel level)
    {
        var payloads = ReadColourPayloads(rom, level);
        if (payloads == null)
            return null;

        var width = level.PixelWidth;
        var height = level.PixelHeight;
        if (width is <= 0 or > MaxImageDimension || height is <= 0 or > MaxImageDimension)
            return null;

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = 0x12;
            rgba[i * 4 + 1] = 0x12;
            rgba[i * 4 + 2] = 0x16;
            rgba[i * 4 + 3] = 0xFF;
        }

        // The maps are painter-ordered planes. Later planes chiefly carry ramps,
        // props and foreground walls over the ground in plane zero.
        foreach (var plane in payloads.Planes)
        {
            for (var y = 0; y < plane.Height; y++)
            for (var x = 0; x < plane.Width; x++)
            {
                var metatile = plane.Metatiles[y * plane.Width + x];
                if (metatile >= payloads.MetatileCount)
                    return null;
                // Map entry zero is the transparent/empty cell. As with tile zero,
                // some ROMs leave its backing table bytes populated; the index is
                // the semantic blank, not the contents at that storage address.
                if (metatile == 0)
                    continue;

                var px = x * ColourMetatilePixels;
                var py = y * ColourMetatilePixels;
                for (var quad = 0; quad < 4; quad++)
                {
                    var tileRef = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(
                        payloads.MetatileTableOffset
                        + metatile * ColourMetatileBytes + quad * 2, 2));
                    var tile = tileRef & 0x3FFF;
                    if (tile >= payloads.TileCount)
                        return null;
                    // Tile zero is the hardware blank. Its backing bytes are not
                    // consistently cleared, so drawing them turns every empty cell
                    // in an overlay plane into an opaque palette-coloured square.
                    if (tile == 0)
                        continue;
                    BlitColourTile(
                        rom, payloads, tile, (tileRef & 0x4000) != 0, (tileRef & 0x8000) != 0,
                        px + quad % 2 * ColourTilePixels,
                        py + quad / 2 * ColourTilePixels,
                        rgba, width, height);
                }
            }
        }

        return new GbaLevelImages.GbaLevelRender(width, height, rgba);
    }

    /// <summary>The level's raw 256-colour BGR555 palette converted to RGBA.</summary>
    public static byte[]? TryGetPalette(ReadOnlySpan<byte> rom, LaterLevel level)
    {
        var rec = level.LevelRecordOffset;
        if (rec < 0 || rec + 0x24 > rom.Length)
            return null;
        var address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(rec + 0x20, 4));
        if (!IsRomPointer(rom, address))
            return null;
        var offset = (int)(address - RomBase);
        if (offset < 0 || offset + PaletteBytes > rom.Length)
            return null;

        var rgba = new byte[PaletteColours * 4];
        for (var i = 0; i < PaletteColours; i++)
        {
            var c = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + i * 2, 2));
            rgba[i * 4] = Expand5(c & 0x1F);
            rgba[i * 4 + 1] = Expand5(c >> 5 & 0x1F);
            rgba[i * 4 + 2] = Expand5(c >> 10 & 0x1F);
            rgba[i * 4 + 3] = 0xFF;
        }

        return rgba;
    }

    /// <summary>
    ///     Renders the one-bit skater-occlusion coverage asset. This is useful for
    ///     analysis, but it is not the level's visible artwork.
    /// </summary>
    public static GbaLevelImages.GbaLevelRender? RenderOcclusionMask(
        ReadOnlySpan<byte> rom, LaterLevel level)
    {
        var p = ReadOcclusionPayloads(rom, level);
        if (p == null)
            return null;

        var width = level.MapWidth * CellPixels;
        var height = level.MapHeight * CellPixels;
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = 0x0A;
            rgba[i * 4 + 1] = 0x0A;
            rgba[i * 4 + 2] = 0x1A;
            rgba[i * 4 + 3] = 0xFF;
        }

        var objectCount = p.Objects.Length / ObjectRecordBytes;
        var cells = level.MapWidth * level.MapHeight;
        var runs = ReadRuns(p.Map, cells, objectCount);
        foreach (var (cell, start, end) in runs)
        {
            var originX = cell % level.MapWidth * CellPixels;
            var originY = cell / level.MapWidth * CellPixels;
            for (var k = start; k < end; k++)
                DrawObject(p, k, originX, originY, rgba, width, height);
        }

        return new GbaLevelImages.GbaLevelRender(width, height, rgba);
    }

    /// <summary>
    ///     Compatibility alias for the original decoder, which was published before
    ///     its payload was identified as an occlusion mask.
    /// </summary>
    public static GbaLevelImages.GbaLevelRender? Render(ReadOnlySpan<byte> rom, LaterLevel level) =>
        RenderOcclusionMask(rom, level);

    private static ColourPayloads? ReadColourPayloads(ReadOnlySpan<byte> rom, LaterLevel level)
    {
        var rec = level.LevelRecordOffset;
        if (rec < 0 || rec + 0x24 > rom.Length)
            return null;

        var tilesWide = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(rec, 2));
        var tilesHigh = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(rec + 2, 2));
        if (tilesWide * ColourTilePixels != level.PixelWidth
            || tilesHigh * ColourTilePixels != level.PixelHeight)
            return null;

        var tileCount = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(rec + 0x04, 2));
        var fourBppCount = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(rec + 0x06, 2));
        if (tileCount == 0 || fourBppCount > tileCount)
            return null;

        var tilePool = PointerToOffset(rom, rec + 0x08);
        var metatileTable = PointerToOffset(rom, rec + 0x0C);
        if (tilePool < 0 || metatileTable < 0)
            return null;

        var planes = new List<ColourPlane>(MaxColourPlanes);
        var maxMetatile = -1;
        for (var i = 0; i < MaxColourPlanes; i++)
        {
            var planeOffset = PointerToOffset(rom, rec + 0x10 + i * 4);
            if (planeOffset < 0)
                continue;
            var plane = ReadColourPlane(rom, planeOffset);
            if (plane == null)
                return null;

            // Plane zero covers the image exactly in 16-pixel metatiles. Overlay
            // maps can carry one guard row/column, which is clipped by the blitter.
            var expectedW = (tilesWide + 1) / 2;
            var expectedH = (tilesHigh + 1) / 2;
            if (planes.Count == 0)
            {
                if (plane.Value.Width != expectedW || plane.Value.Height != expectedH)
                    return null;
            }
            else if (plane.Value.Width is < 1 || plane.Value.Width > expectedW + 1
                     || plane.Value.Height is < 1 || plane.Value.Height > expectedH + 1)
            {
                return null;
            }

            foreach (var metatile in plane.Value.Metatiles)
                maxMetatile = Math.Max(maxMetatile, metatile);
            planes.Add(plane.Value);
        }

        if (planes.Count == 0 || maxMetatile < 0)
            return null;
        var metatileCount = maxMetatile + 1;
        if ((long)metatileTable + (long)metatileCount * ColourMetatileBytes > rom.Length)
            return null;

        // The tile pool concatenates 4bpp pixels, 8bpp pixels, one u16 remap id
        // per 4bpp tile, aligns to a word, then stores 16-byte remap palettes.
        var eightBppOffset = (long)tilePool + (long)fourBppCount * FourBppTileBytes;
        var paletteIdsOffset = eightBppOffset + (long)(tileCount - fourBppCount) * EightBppTileBytes;
        var paletteRemapsOffset = (paletteIdsOffset + fourBppCount * 2L + 3) & ~3L;
        if (paletteIdsOffset < 0 || paletteRemapsOffset > rom.Length)
            return null;

        var maxRemap = -1;
        for (var tile = 0; tile < fourBppCount; tile++)
        {
            var idOffset = paletteIdsOffset + tile * 2L;
            if (idOffset + 2 > rom.Length)
                return null;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice((int)idOffset, 2));
            maxRemap = Math.Max(maxRemap, id);
        }

        if (maxRemap >= 0 && paletteRemapsOffset + (long)(maxRemap + 1) * 16 > rom.Length)
            return null;

        // A used metatile must resolve entirely within the declared tile pool.
        for (var metatile = 0; metatile < metatileCount; metatile++)
        for (var quad = 0; quad < 4; quad++)
        {
            var tileRef = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(
                metatileTable + metatile * ColourMetatileBytes + quad * 2, 2));
            if ((tileRef & 0x3FFF) >= tileCount)
                return null;
        }

        var palette = TryGetPalette(rom, level);
        return palette == null
            ? null
            : new ColourPayloads(
                tileCount, fourBppCount, tilePool, (int)paletteIdsOffset,
                (int)paletteRemapsOffset, metatileTable, metatileCount, palette, planes);
    }

    private static ColourPlane? ReadColourPlane(ReadOnlySpan<byte> rom, int offset)
    {
        if (offset < 0 || offset + 4 > rom.Length)
            return null;
        var width = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 2, 2));
        if (width == 0 || height == 0
            || width > MaxImageDimension / ColourMetatilePixels + 1
            || height > MaxImageDimension / ColourMetatilePixels + 1)
            return null;

        var rowTableBytes = (long)height * 4;
        var streamBaseLong = (long)offset + 4 + rowTableBytes;
        if (streamBaseLong > rom.Length)
            return null;
        var metatiles = new ushort[width * height];

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
                    metatiles[y * width + x++] = command;
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
                    metatiles.AsSpan(y * width + x, count).Fill(value);
                    x += count;
                    continue;
                }

                var literalBytes = count * 2;
                if (cursor + literalBytes > rom.Length)
                    return null;
                for (var i = 0; i < count; i++)
                    metatiles[y * width + x + i] = BinaryPrimitives.ReadUInt16LittleEndian(
                        rom.Slice(cursor + i * 2, 2));
                cursor += literalBytes;
                x += count;
            }
        }

        return new ColourPlane(width, height, metatiles);
    }

    private static void BlitColourTile(
        ReadOnlySpan<byte> rom,
        ColourPayloads payloads,
        int tile,
        bool flipX,
        bool flipY,
        int px,
        int py,
        byte[] rgba,
        int width,
        int height)
    {
        var isFourBpp = tile < payloads.FourBppTileCount;
        var tileOffset = isFourBpp
            ? payloads.TilePoolOffset + tile * FourBppTileBytes
            : payloads.TilePoolOffset + payloads.FourBppTileCount * FourBppTileBytes
              + (tile - payloads.FourBppTileCount) * EightBppTileBytes;
        var remapOffset = 0;
        if (isFourBpp)
        {
            var remap = BinaryPrimitives.ReadUInt16LittleEndian(
                rom.Slice(payloads.PaletteIdsOffset + tile * 2, 2));
            remapOffset = payloads.PaletteRemapsOffset + remap * 16;
        }

        for (var y = 0; y < ColourTilePixels; y++)
        {
            var destY = py + y;
            if (destY < 0 || destY >= height)
                continue;
            var sourceY = flipY ? ColourTilePixels - 1 - y : y;
            for (var x = 0; x < ColourTilePixels; x++)
            {
                var destX = px + x;
                if (destX < 0 || destX >= width)
                    continue;
                var sourceX = flipX ? ColourTilePixels - 1 - x : x;
                int paletteIndex;
                if (isFourBpp)
                {
                    // These are not in the usual GBA 4bpp left-to-right order.
                    // The cartridge's ARM tile expander consumes each 32-bit row
                    // from its most-significant nibble down to its least-significant
                    // nibble before writing the 8bpp VRAM tile. Mirroring each row
                    // here reproduces that path; treating the pack as ordinary GBA
                    // tiles horizontally mirrors every 8-pixel tile and produces
                    // the characteristic diagonal/sawtooth shear at tile seams.
                    var packed = rom[tileOffset + sourceY * 4 + (3 - sourceX / 2)];
                    var local = sourceX % 2 == 0 ? packed >> 4 : packed & 0x0F;
                    paletteIndex = rom[remapOffset + local];
                }
                else
                {
                    paletteIndex = rom[tileOffset + sourceY * ColourTilePixels + sourceX];
                }

                if (paletteIndex == 0)
                    continue;
                var dest = (destY * width + destX) * 4;
                rgba[dest] = payloads.Palette[paletteIndex * 4];
                rgba[dest + 1] = payloads.Palette[paletteIndex * 4 + 1];
                rgba[dest + 2] = payloads.Palette[paletteIndex * 4 + 2];
                rgba[dest + 3] = 0xFF;
            }
        }
    }

    private static int PointerToOffset(ReadOnlySpan<byte> rom, int pointerOffset)
    {
        if (pointerOffset < 0 || pointerOffset + 4 > rom.Length)
            return -1;
        var address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(pointerOffset, 4));
        return IsRomPointer(rom, address) ? (int)(address - RomBase) : -1;
    }

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));

    /// <summary>
    ///     The map's runs as <c>(cell, firstObject, endObject)</c>. A non-empty cell
    ///     owns every object up to the next non-empty cell's start.
    /// </summary>
    private static List<(int Cell, int Start, int End)> ReadRuns(byte[] map, int cells, int objectCount)
    {
        var starts = new List<(int Cell, int Start)>();
        for (var c = 0; c < cells && c * 2 + 2 <= map.Length; c++)
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(map.AsSpan(c * 2, 2));
            if (v != EmptyCell && v < objectCount)
                starts.Add((c, v));
        }

        var runs = new List<(int, int, int)>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Start : objectCount;
            if (end > starts[i].Start)
                runs.Add((starts[i].Cell, starts[i].Start, end));
        }

        return runs;
    }

    private static void DrawObject(
        OcclusionPayloads p, int k, int originX, int originY, byte[] rgba, int width, int height)
    {
        var rec = p.Objects.AsSpan(k * ObjectRecordBytes, ObjectRecordBytes);
        var gridW = (rec[2] - rec[0]) / MetatilePixels;
        var gridH = (rec[3] - rec[1]) / MetatilePixels;
        if (gridW <= 0 || gridH <= 0)
            return;

        // The record states its grid as a u16 BYTE offset into the grid pool.
        var baseCell = BinaryPrimitives.ReadUInt16LittleEndian(rec[8..10]) / 2;
        var metatileCount = p.Metatiles.Length / MetatileBytes;
        var elementCount = p.Elements.Length / ElementBytes;

        for (var gy = 0; gy < gridH; gy++)
        for (var gx = 0; gx < gridW; gx++)
        {
            var index = baseCell + gy * gridW + gx;
            if (index < 0 || index * 2 + 2 > p.Grids.Length)
                continue;
            var metatile = BinaryPrimitives.ReadUInt16LittleEndian(p.Grids.AsSpan(index * 2, 2));
            if (metatile >= metatileCount)
                continue;

            for (var quad = 0; quad < 4; quad++)
            {
                var element = BinaryPrimitives.ReadUInt16LittleEndian(
                    p.Metatiles.AsSpan(metatile * MetatileBytes + quad * 2, 2));
                if (element >= elementCount)
                    continue;
                var px = originX + rec[0] + gx * MetatilePixels + quad % 2 * ElementPixels;
                var py = originY + rec[1] + gy * MetatilePixels + quad / 2 * ElementPixels;
                BlitElement(p.Elements, element, px, py, rgba, width, height);
            }
        }
    }

    private static void BlitElement(
        byte[] elements, int element, int px, int py, byte[] rgba, int width, int height)
    {
        for (var row = 0; row < ElementPixels; row++)
        {
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(
                elements.AsSpan(element * ElementBytes + row * 2, 2));
            if (bits == 0)
                continue;
            var y = py + row;
            if (y < 0 || y >= height)
                continue;
            for (var col = 0; col < ElementPixels; col++)
            {
                if ((bits >> col & 1) == 0)
                    continue;
                var x = px + col;
                if (x < 0 || x >= width)
                    continue;
                var o = (y * width + x) * 4;
                rgba[o] = 0xE6;
                rgba[o + 1] = 0xE6;
                rgba[o + 2] = 0xEB;
            }
        }
    }

    private static OcclusionPayloads? ReadOcclusionPayloads(ReadOnlySpan<byte> rom, LaterLevel level)
    {
        var rec = level.ArtRecordOffset;
        if (rec < 0 || rec + ArtRecordStride > rom.Length)
            return null;

        // The elements are always compressed; every other field follows a byte of the
        // flags word. A raw field carries no length, so each is sized by whatever
        // references it, which is why they are read in dependency order: the map from
        // the level's own cell count, the object table from the map's highest index,
        // the grid pool from the furthest grid any object reaches, and the metatile
        // table from the grid pool's highest index.
        var elements = ReadCompressed(rom, rec + 0x10);
        if (elements == null)
            return null;

        var cells = level.MapWidth * level.MapHeight;
        var map = ReadField(rom, rec, 0x00, 0x18, cells * 2);
        if (map == null)
            return null;

        var objectCount = HighestIndex(map, EmptyCell) + 1;
        var objects = ReadField(rom, rec, 0x04, 0x19, objectCount * ObjectRecordBytes);
        if (objects == null || objects.Length < objectCount * ObjectRecordBytes)
            return null;

        var grids = ReadField(rom, rec, 0x08, level.GridFlagOffset, GridPoolBytes(objects, objectCount));
        if (grids == null)
            return null;

        var metatileCount = HighestIndex(grids, -1) + 1;
        var metatiles = ReadField(rom, rec, 0x0C, 0x1A, metatileCount * MetatileBytes);
        if (metatiles == null || metatiles.Length < metatileCount * MetatileBytes)
            return null;

        return new OcclusionPayloads(map, objects, grids, metatiles, elements);
    }

    /// <summary>Highest u16 in the buffer, ignoring <paramref name="ignore" />.</summary>
    private static int HighestIndex(byte[] data, int ignore)
    {
        var max = -1;
        for (var i = 0; i + 2 <= data.Length; i += 2)
        {
            int v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i, 2));
            if (v != ignore && v > max)
                max = v;
        }

        return max;
    }

    private static byte[]? ReadCompressed(ReadOnlySpan<byte> rom, int pointerOffset)
    {
        var address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(pointerOffset, 4));
        if (!IsRomPointer(rom, address))
            return null;
        return GbaBiosLz77.TryDecompress(rom, (int)(address - RomBase), out var payload, out _)
            ? payload
            : null;
    }

    private static byte[]? ReadField(
        ReadOnlySpan<byte> rom, int record, int pointerOffset, int flagOffset, int rawLength)
    {
        var address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(record + pointerOffset, 4));
        if (!IsRomPointer(rom, address))
            return null;
        var offset = (int)(address - RomBase);
        if (flagOffset == AlwaysCompressed || rom[record + flagOffset] != 0)
            return GbaBiosLz77.TryDecompress(rom, offset, out var payload, out _) ? payload : null;
        if (rawLength <= 0 || offset + rawLength > rom.Length)
            return null;
        return rom.Slice(offset, rawLength).ToArray();
    }

    /// <summary>
    ///     The grid pool's length in bytes: the furthest cell any object reaches. An
    ///     object's record states its grid as a byte offset and its own width and
    ///     height, so the pool has to cover the last of them.
    /// </summary>
    private static int GridPoolBytes(byte[] objects, int objectCount)
    {
        var cells = 0;
        for (var k = 0; k < objectCount; k++)
        {
            var rec = objects.AsSpan(k * ObjectRecordBytes, ObjectRecordBytes);
            var w = (rec[2] - rec[0]) / MetatilePixels;
            var h = (rec[3] - rec[1]) / MetatilePixels;
            if (w <= 0 || h <= 0)
                continue;
            var end = BinaryPrimitives.ReadUInt16LittleEndian(rec[8..10]) / 2 + w * h;
            if (end > cells)
                cells = end;
        }

        return cells * 2;
    }

    /// <summary>
    ///     Decides whether this cartridge's loader gates the grid pool on the flags
    ///     word's fourth byte. Two readings are possible and the ROM settles it: the
    ///     pool either decompresses in every record (no gate), or it decompresses in
    ///     exactly the records whose fourth flag byte is set. Anything else declines.
    /// </summary>
    private static int MeasureGridFlag(ReadOnlySpan<byte> rom, int table, int count)
    {
        var alwaysHolds = true;
        var flagHolds = true;
        var flagVaries = false;
        for (var i = 0; i < count; i++)
        {
            var art = table + i * ArtRecordStride;
            var compressed = ReadCompressed(rom, art + 0x08) != null;
            var flagged = rom[art + 0x1B] != 0;
            if (!compressed)
                alwaysHolds = false;
            if (compressed != flagged)
                flagHolds = false;
            if (flagged)
                flagVaries = true;
        }

        if (alwaysHolds)
            return AlwaysCompressed;
        return flagHolds && flagVaries ? 0x1B : int.MinValue;
    }

    /// <summary>The map's cell count along an axis: the loader's own rounding.</summary>
    private static int MapCells(int pixels) => (pixels + CellPixels / 2) / CellPixels;

    /// <summary>
    ///     The level's pixel size, which the record stores divided by 8 (the loader
    ///     scales it back up before deriving the map width).
    /// </summary>
    private static (int Width, int Height) ReadPixelSize(ReadOnlySpan<byte> rom, int record)
    {
        var w = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(record, 2));
        var h = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(record + 2, 2));
        return (w * 8, h * 8);
    }

    /// <summary>
    ///     Finds the constant offset from a level record's start to its art pointer,
    ///     using the records whose map is compressed: there the map's own cell count
    ///     must equal the product of the two map dimensions the record states.
    /// </summary>
    private static int MeasurePointerDelta(ReadOnlySpan<byte> rom, int table, int count)
    {
        var votes = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
        {
            var art = table + i * ArtRecordStride;
            if (rom[art + 0x18] == 0)
                continue; // map stored raw: its cell count cannot referee a candidate
            var map = ReadCompressed(rom, art + 0x00);
            if (map == null)
                continue;
            var cells = map.Length / 2;
            var site = FindPointerSite(rom, RomBase + (uint)art);
            if (site < 0)
                continue;

            for (var delta = 0; delta <= 0x60; delta += 2)
            {
                var record = site - delta;
                if (record < 0 || record + 4 > rom.Length)
                    continue;
                var (w, h) = ReadPixelSize(rom, record);
                if (w is < 64 or > 32000 || h is < 64 or > 32000)
                    continue;
                if (MapCells(w) * MapCells(h) != cells)
                    continue;
                votes[delta] = votes.GetValueOrDefault(delta) + 1;
            }
        }

        var best = -1;
        var bestVotes = 0;
        foreach (var (delta, v) in votes)
        {
            if (v <= bestVotes)
                continue;
            bestVotes = v;
            best = delta;
        }

        return bestVotes >= 2 ? best : -1;
    }

    private static int FindPointerSite(ReadOnlySpan<byte> rom, uint address)
    {
        for (var offset = 0; offset + 4 <= rom.Length; offset += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4)) == address)
                return offset;
        }

        return -1;
    }

    /// <summary>
    ///     Locates the art table by shape: the longest run of 0x1C-strided records
    ///     whose six pointers are all in-ROM, whose flags word is small, and whose
    ///     grid and element fields both decompress (those two are never stored raw).
    /// </summary>
    private static int LocateTable(ReadOnlySpan<byte> rom, out int count)
    {
        count = 0;
        var best = -1;

        // Every candidate is collected before any run is measured: advancing past a
        // run by its own length can step over the real table's first record when a
        // spurious single-record hit lands just before it.
        var hits = new HashSet<int>();
        for (var offset = 0; offset + ArtRecordStride <= rom.Length; offset += 4)
        {
            if (IsArtRecord(rom, offset))
                hits.Add(offset);
        }

        foreach (var start in hits)
        {
            if (hits.Contains(start - ArtRecordStride))
                continue; // not the head of its run
            var run = 1;
            while (hits.Contains(start + run * ArtRecordStride))
                run++;
            if (run <= count)
                continue;
            count = run;
            best = start;
        }

        if (count < MinTableRecords)
        {
            count = 0;
            return -1;
        }

        return best;
    }

    private static bool IsArtRecord(ReadOnlySpan<byte> rom, int offset)
    {
        if (offset < 0 || offset + ArtRecordStride > rom.Length)
            return false;
        for (var i = 0; i < 6; i++)
        {
            if (!IsRomPointer(rom, BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset + i * 4, 4))))
                return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset + 0x18, 4)) >= 0x10000000)
            return false;

        // The element pool is the one field no cartridge stores raw, so requiring it
        // to decompress is what keeps ordinary pointer runs out. Gating on any other
        // field would drop the records that store theirs uncompressed.
        return ReadCompressed(rom, offset + 0x10) != null;
    }

    private static bool IsRomPointer(ReadOnlySpan<byte> rom, uint address) =>
        address >= RomBase && address < RomBase + (uint)rom.Length;
}
