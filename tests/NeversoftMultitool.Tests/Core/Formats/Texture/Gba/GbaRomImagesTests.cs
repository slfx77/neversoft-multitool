using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Texture.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Gba;

/// <summary>
///     Pins the GBA BIOS-LZ77 codec and the full-screen image scanner against
///     Tony Hawk's Pro Skater 2 (GBA): the 13 anonymous, table-addressed screens
///     (7 linear mode-4 bitmaps + 6 tiled backdrops), each paired with the nearest
///     preceding palette that fits and decoded to a pinned RGBA digest. THPS2 is the
///     only cart shipping these images; the other six carve nothing here.
/// </summary>
public sealed class GbaRomImagesTests(TestPaths paths)
{
    [Fact]
    public void BiosLz77_RoundTripsLiterals()
    {
        // 32 literal bytes: header + four flag groups of eight literals.
        var stream = new List<byte> { 0x10, 32, 0, 0 };
        var expected = new byte[32];
        for (var g = 0; g < 4; g++)
        {
            stream.Add(0x00); // all-literal flag group
            for (var k = 0; k < 8; k++)
            {
                var v = (byte)(g * 8 + k);
                expected[g * 8 + k] = v;
                stream.Add(v);
            }
        }

        Assert.True(GbaBiosLz77.TryDecompress(stream.ToArray(), 0, out var payload, out var compLen));
        Assert.Equal(expected, payload);
        Assert.Equal(stream.Count, compLen);
    }

    [Fact]
    public void BiosLz77_DecodesBackReference()
    {
        // 16 literals (0..15), then one back-reference (disp 15, length 16) that
        // repeats them — the second half copies the first.
        var stream = new byte[]
        {
            0x10, 32, 0, 0,
            0x00, 0, 1, 2, 3, 4, 5, 6, 7,
            0x00, 8, 9, 10, 11, 12, 13, 14, 15,
            0x80, 0xD0, 0x0F // flag: bit7=back-ref; length nibble 13(+3)=16, disp 15
        };

        Assert.True(GbaBiosLz77.TryDecompress(stream, 0, out var payload, out _));
        var expected = new byte[32];
        for (var i = 0; i < 16; i++)
            expected[i] = expected[i + 16] = (byte)i;
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void BiosLz77_RejectsNonStreamAndTruncation()
    {
        // Wrong type byte.
        Assert.False(GbaBiosLz77.TryDecompress(new byte[] { 0x20, 32, 0, 0, 0 }, 0, out _, out _));
        // Declares 32 bytes but the token data runs out.
        Assert.False(GbaBiosLz77.TryDecompress(new byte[] { 0x10, 32, 0, 0, 0x00, 1, 2 }, 0, out _, out _));
        // Back-reference before anything has been written (src < 0).
        Assert.False(GbaBiosLz77.TryDecompress(new byte[] { 0x10, 32, 0, 0, 0x80, 0xD0, 0x0F }, 0, out _, out _));
    }

    [Fact]
    public void Thps2_Extracts13FullScreenImages_WithPinnedDigest()
    {
        var path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");
        Assert.SkipWhen(path == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(path!);

        var images = GbaRomImages.ScanFullScreenImages(rom);
        Assert.Equal(13, images.Count);

        // ROM-ordered offsets, and the layout split (7 linear logos/cards, then 6
        // tiled menu backdrops). The reduced-palette Vicarious Visions logo pairs
        // with the 41-colour palette rather than the preceding 256-colour one.
        int[] expectedOffsets =
        [
            0x0042FC4C, 0x00433808, 0x00435260, 0x0043745C, 0x0043EFE4, 0x00446008, 0x0044D8C4,
            0x00455FEC, 0x0045E210, 0x00467C20, 0x00470904, 0x004789C4, 0x00480CA4
        ];
        Assert.Equal(expectedOffsets, images.Select(i => i.RomOffset).ToArray());
        Assert.Equal(7, images.Count(i => i.Layout == GbaRomImages.ImageLayout.Linear));
        Assert.Equal(6, images.Count(i => i.Layout == GbaRomImages.ImageLayout.Tiled));

        var vv = images[1];
        Assert.Equal(GbaRomImages.ImageLayout.Linear, vv.Layout);
        Assert.Equal(0x004337A4, vv.PaletteOffset);
        Assert.Equal(41, vv.PaletteColors);

        foreach (var image in images)
            Assert.Equal(GbaRomImages.ScreenWidth * GbaRomImages.ScreenHeight * 4, image.Rgba.Length);

        using var sha = SHA256.Create();
        foreach (var image in images)
            sha.TransformBlock(image.Rgba, 0, image.Rgba.Length, null, 0);
        sha.TransformFinalBlock([], 0, 0);
        Assert.Equal(
            "41b6b6f896f0b0019ead7ed39edcc7129a4ee3afef08e6898541ef9d2434c604",
            Convert.ToHexStringLower(sha.Hash!));
    }

    // Only THPS2 packs full-screen art as BIOS-LZ77 mode-4 / tiled screens; the
    // later carts moved their art to a different packaging, so the scanner is
    // correctly empty for them (a pin on the engine-evolution divergence).
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", 13)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)", 0)]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", 0)]
    [InlineData("Tony Hawk's Underground (2003-10-27, GBA - Final)", 0)]
    [InlineData("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", 0)]
    [InlineData("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", 0)]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", 0)]
    public void FullScreenImageCountAcrossTheGbaLine(string build, int expected)
    {
        var path = paths.FindSampleFiles(build, "*.gba").FirstOrDefault();
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        var rom = File.ReadAllBytes(path!);
        Assert.Equal(expected, GbaRomImages.ScanFullScreenImages(rom).Count);
    }
}
