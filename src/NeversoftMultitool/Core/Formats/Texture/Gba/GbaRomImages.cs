using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Texture.Gba;

/// <summary>
///     Extracts the full-screen menu / title / logo / level-select images that the
///     Vicarious Visions GBA Tony Hawk engine stores as BIOS-LZ77 streams
///     (<see cref="GbaBiosLz77" />). The carts carry no filenames — art is reached
///     through anonymous pointer tables — so images are located by content: a stream
///     that decodes to exactly <c>240 × 160</c> bytes is a full-screen 8-bit-paletted
///     screen, and its 15-bit BGR palette is the nearest preceding LZ77 stream large
///     enough to cover the indices the image uses (a full 256-colour palette is 512
///     bytes; reduced-palette art — e.g. the two-tone studio logo — ships a shorter
///     one, which is why nearest-preceding-that-fits, not nearest-512, is the rule).
///
///     A screen is stored in one of two pixel orders and the engine mixes them, so
///     the layout is decided per image by <see cref="ScoreLayout" />: <b>linear</b>
///     framebuffer order (mode-4 bitmap backgrounds) versus <b>tiled</b> 8×8 order
///     (mode-0 tiled backgrounds, 30 × 20 tiles). The correct order is the one that
///     minimises horizontal neighbour deltas — the wrong reading interleaves tile
///     rows into every scanline and reads as high-frequency streaking (the same
///     "pick the layout that stays continuous" test the .fnt nibble-order picker uses).
///
///     Verified against Tony Hawk's Pro Skater 2 (GBA): 13 screens — 7 linear
///     (the Activision / Vicarious Visions logos, the legal screen, the title, both
///     competition-invite cards, the Rooftops art) and 6 tiled menu backdrops.
///     The other six carts carry no BIOS-LZ77 full-screen images (their art moved to
///     a different packaging), so this scanner returns an empty list for them.
/// </summary>
public static class GbaRomImages
{
    public const int ScreenWidth = 240;
    public const int ScreenHeight = 160;
    private const int ScreenPixels = ScreenWidth * ScreenHeight; // 38400
    private const int TilesWide = ScreenWidth / 8;               // 30
    private const int MaxPaletteBytes = 512;                     // 256 × 15-bit

    public enum ImageLayout
    {
        Linear,
        Tiled
    }

    public readonly record struct GbaScreenImage(
        int RomOffset, int PaletteOffset, int PaletteColors, ImageLayout Layout, byte[] Rgba)
    {
        /// <summary>A stable, offset-derived name (the assets are anonymous in the ROM).</summary>
        public string Name => $"gba_image_{RomOffset:X8}";
    }

    /// <summary>
    ///     Segments the ROM into non-overlapping BIOS-LZ77 streams and returns every
    ///     full-screen 8-bit image, each decoded to RGBA with its paired palette.
    /// </summary>
    public static List<GbaScreenImage> ScanFullScreenImages(ReadOnlySpan<byte> rom)
    {
        var palettes = new List<(int Offset, byte[] Data)>();
        var bitmaps = new List<(int Offset, byte[] Data)>();

        var i = 0;
        while (i + 4 <= rom.Length)
        {
            if (rom[i] == 0x10 && GbaBiosLz77.TryDecompress(rom, i, out var payload, out var compLen))
            {
                if (payload.Length == ScreenPixels)
                    bitmaps.Add((i, payload));
                else if (payload.Length is >= 2 and <= MaxPaletteBytes && payload.Length % 2 == 0)
                    palettes.Add((i, payload));
                // Skip the stream's own bytes: an overlapping 0x10 inside a valid
                // stream is that stream's data, not a new asset.
                i += (compLen + 3) & ~3;
                continue;
            }

            i += 4;
        }

        var results = new List<GbaScreenImage>(bitmaps.Count);
        foreach (var (offset, data) in bitmaps)
        {
            var colorsNeeded = MaxIndex(data) + 1;
            var pal = NearestPrecedingPalette(palettes, offset, colorsNeeded);
            if (pal is null)
                continue; // no palette large enough -> cannot be faithfully coloured
            var layout = ScoreLayout(data, ImageLayout.Linear) <= ScoreLayout(data, ImageLayout.Tiled)
                ? ImageLayout.Linear
                : ImageLayout.Tiled;
            var rgba = DecodeRgba(data, pal.Value.Data, layout);
            results.Add(new GbaScreenImage(offset, pal.Value.Offset, pal.Value.Data.Length / 2, layout, rgba));
        }

        return results;
    }

    /// <summary>Decodes one full-screen 8-bit image to RGBA under the given pixel order.</summary>
    public static byte[] DecodeRgba(ReadOnlySpan<byte> image, ReadOnlySpan<byte> palette, ImageLayout layout)
    {
        var colors = palette.Length / 2;
        var rgba = new byte[ScreenPixels * 4];
        for (var y = 0; y < ScreenHeight; y++)
        for (var x = 0; x < ScreenWidth; x++)
        {
            var index = layout == ImageLayout.Linear ? image[y * ScreenWidth + x] : image[TiledByteIndex(x, y)];
            var o = (y * ScreenWidth + x) * 4;
            if (index < colors)
            {
                var c = palette[index * 2] | (palette[index * 2 + 1] << 8);
                rgba[o] = Expand5((c) & 0x1F);
                rgba[o + 1] = Expand5((c >> 5) & 0x1F);
                rgba[o + 2] = Expand5((c >> 10) & 0x1F);
            }

            rgba[o + 3] = 0xFF;
        }

        return rgba;
    }

    // Mean absolute horizontal palette-index delta under the candidate order,
    // sampling alternate rows. Lower = smoother = the order the art was stored in.
    private static double ScoreLayout(ReadOnlySpan<byte> image, ImageLayout layout)
    {
        long total = 0;
        var n = 0;
        for (var y = 0; y < ScreenHeight; y += 2)
        {
            var prev = layout == ImageLayout.Linear ? image[y * ScreenWidth] : image[TiledByteIndex(0, y)];
            for (var x = 1; x < ScreenWidth; x++)
            {
                var cur = layout == ImageLayout.Linear ? image[y * ScreenWidth + x] : image[TiledByteIndex(x, y)];
                total += Math.Abs(cur - prev);
                prev = cur;
                n++;
            }
        }

        return n == 0 ? 0 : (double)total / n;
    }

    private static int TiledByteIndex(int x, int y)
    {
        var tile = (y / 8) * TilesWide + (x / 8);
        return tile * 64 + (y % 8) * 8 + (x % 8);
    }

    private static (int Offset, byte[] Data)? NearestPrecedingPalette(
        List<(int Offset, byte[] Data)> palettes, int bitmapOffset, int colorsNeeded)
    {
        (int Offset, byte[] Data)? best = null;
        foreach (var p in palettes)
        {
            if (p.Offset >= bitmapOffset || p.Data.Length / 2 < colorsNeeded)
                continue;
            if (best is null || p.Offset > best.Value.Offset)
                best = p;
        }

        return best;
    }

    private static int MaxIndex(ReadOnlySpan<byte> data)
    {
        var max = 0;
        foreach (var b in data)
            if (b > max)
                max = b;
        return max;
    }

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
}
