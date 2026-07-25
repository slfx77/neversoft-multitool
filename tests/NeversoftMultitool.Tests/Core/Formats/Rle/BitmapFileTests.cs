using NeversoftMultitool.Core.Formats.Rle;

namespace NeversoftMultitool.Tests.Core.Formats.Rle;

public class BitmapFileTests(TestPaths paths)
{
    private const string Thps2Dc = "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)";
    private const string Thug2Ps2 = "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";

    [Fact]
    public void Convert_StandardBmp_DecodesViaImageSharp()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var bmpPath = paths.FindSampleFile(Thps2Dc, "ABUTTON.BMP");
        Assert.SkipWhen(bmpPath == null, "ABUTTON.BMP not available");

        var result = BitmapFile.Convert(File.ReadAllBytes(bmpPath!), bmpPath!);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.RgbaPixels);
        Assert.True(result.Width > 0 && result.Height > 0);
        Assert.Equal(result.Width * result.Height * 4, result.RgbaPixels!.Length);
    }

    [Fact]
    public void Convert_ThirtyTwoBitTga_PreservesAlpha()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // THUG2 ships thps3_bannericon_01.tga as a type-2 truecolor TGA with
        // an 8-bit alpha channel (32bpp).
        var tgaPath = paths.FindSampleFile(Thug2Ps2, "thps3_bannericon_01.tga");
        Assert.SkipWhen(tgaPath == null, "thps3_bannericon_01.tga not available");

        var result = BitmapFile.Convert(File.ReadAllBytes(tgaPath!), tgaPath!);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.RgbaPixels);
        Assert.Equal(96, result.Width);
        Assert.Equal(32, result.Height);

        // Alpha must survive the decode: a banner icon has transparent pixels.
        var hasTransparency = false;
        for (var i = 3; i < result.RgbaPixels!.Length; i += 4)
        {
            if (result.RgbaPixels[i] == 255) continue;
            hasTransparency = true;
            break;
        }

        Assert.True(hasTransparency, "Expected at least one non-opaque alpha value in 32-bit TGA");
    }

    [Fact]
    public void Convert_ShortPaletteBmp_RepairsAndDecodes()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // THPS2 DC LOADBAR.BMP declares biClrUsed=255 (spec-legal) but
        // ImageSharp rejects it; BitmapFile pads the palette and retries.
        var bmpPath = paths.FindSampleFile(Thps2Dc, "LOADBAR.BMP");
        Assert.SkipWhen(bmpPath == null, "LOADBAR.BMP not available");

        var result = BitmapFile.Convert(File.ReadAllBytes(bmpPath!), bmpPath!);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(416, result.Width);
        Assert.Equal(13, result.Height);
    }

    [Theory]
    [InlineData("foo.bmp", true)]
    [InlineData("FOO.TGA", true)]
    [InlineData("foo.rle", false)]
    [InlineData("foo.bmr", false)]
    [InlineData("foo.zlb", false)]
    public void HasSelfDescribedDimensions_MatchesExtension(string name, bool expected)
    {
        Assert.Equal(expected, BitmapFile.HasSelfDescribedDimensions(name));
        Assert.True(BitmapFile.IsSupportedExtension(name));
    }

    [Fact]
    public void Convert_NeversoftExtensions_StillRouteThroughRleImage()
    {
        // Garbage bytes with an .rle name must fail through the RLE path
        // (magic check), not the ImageSharp path.
        var result = BitmapFile.Convert([0x00, 0x01, 0x02, 0x03], "garbage.rle");
        Assert.False(result.Success);
    }
}