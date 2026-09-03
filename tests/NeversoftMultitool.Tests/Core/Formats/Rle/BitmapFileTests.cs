using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Rle;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Rle;

public class BitmapFileTests(TestPaths paths)
{
    private const string Thps2Dc = "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)";
    private const string Thug2Ps2 = "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";

    [CorpusFact]
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

    [CorpusFact]
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

    [CorpusFact]
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
    [InlineData("foo.png", true)]
    [InlineData("FOO.JPG", true)]
    [InlineData("foo.jpeg", true)]
    [InlineData("FOO.GIF", true)]
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

    [Theory]
    [InlineData("image.png")]
    [InlineData("image.jpg")]
    [InlineData("image.jpeg")]
    [InlineData("image.gif")]
    public void Convert_StandardWebImage_DecodesWithExtensionSpecificDecoder(string name)
    {
        var data = EncodeStandardImage(name);

        Assert.Equal(2, BitmapFile.DetectWidth(data, name));
        var result = BitmapFile.Convert(data, name);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal(8, result.RgbaPixels!.Length);
    }

    [Fact]
    public void Convert_MislabeledStandardWebImage_FailsClosed()
    {
        var png = EncodeStandardImage("image.png");

        Assert.Equal(0, BitmapFile.DetectWidth(png, "image.jpg"));
        Assert.False(BitmapFile.Convert(png, "image.jpg").Success);
    }

    [CorpusFact]
    public void Convert_AllNonEmptyStandardWebImagesAndRejectsEmptyCorpusRecords()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = Directory.EnumerateDirectories(paths.SampleBuildsDir!)
            .Where(static build => !Path.GetFileName(build).Contains(", GBA -", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static build => new[] { "*.png", "*.jpg", "*.jpeg", "*.gif" }
                .SelectMany(pattern => Directory.EnumerateFiles(build, pattern, SearchOption.AllDirectories)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(4_477, files.Length);
        var failures = new List<string>();
        var emptyRejected = 0;
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            var result = BitmapFile.Convert(data, file);
            if (data.Length == 0)
            {
                if (!result.Success && BitmapFile.DetectWidth(data, file) == 0)
                    emptyRejected++;
                else
                    failures.Add($"{Path.GetRelativePath(paths.SampleBuildsDir!, file)}: empty file was accepted");
                continue;
            }

            if (!result.Success || result.Width <= 0 || result.Height <= 0
                || BitmapFile.DetectWidth(data, file) != result.Width)
            {
                failures.Add($"{Path.GetRelativePath(paths.SampleBuildsDir!, file)}: " +
                             (result.ErrorMessage ?? $"invalid {result.Width}x{result.Height} result"));
            }
        }

        Assert.Equal(22, emptyRejected);
        Assert.True(failures.Count == 0,
            $"Failed {failures.Count}/{files.Length}: {string.Join(" | ", failures.Take(20))}");
    }

    [Fact]
    public void Convert_N64FullscreenImage_DecodesSelfDescribedRgba()
    {
        var data = BuildN64ImageRecord();

        Assert.True(BitmapFile.IsSupportedExtension("splash.IMG.N64"));
        Assert.True(BitmapFile.HasSelfDescribedDimensions("splash.img.n64"));
        Assert.Equal(1, BitmapFile.DetectWidth(data, "splash.img.n64"));

        var result = BitmapFile.Convert(data, "splash.img.n64", 99);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, result.RgbaPixels);
    }

    [Fact]
    public void Convert_MalformedN64FullscreenImage_FailsWithoutRleFallback()
    {
        var result = BitmapFile.Convert([0, 1, 2, 3], "broken.img.n64", 1);

        Assert.False(result.Success);
        Assert.Equal("Failed to decode N64 image record", result.ErrorMessage);
    }

    [Fact]
    public void Convert_N64FullscreenImageWithOverflowingStride_FailsClosed()
    {
        var data = BuildN64ImageRecord();
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(32), 2);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(40), int.MaxValue);

        Assert.Equal(0, BitmapFile.DetectWidth(data, "broken.img.n64"));
        var result = BitmapFile.Convert(data, "broken.img.n64");
        Assert.False(result.Success);
        Assert.Equal("Failed to decode N64 image record", result.ErrorMessage);
    }

    private static byte[] BuildN64ImageRecord()
    {
        var data = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(data, 3);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), 20);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 48);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), 50);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), 51);

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 0x00080410);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(24), 3);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(32), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(36), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(44), 0);

        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 0xF801);
        data[50] = 0;
        return data;
    }

    private static byte[] EncodeStandardImage(string name)
    {
        using var image = new Image<Rgba32>(2, 1, new Rgba32(20, 40, 60, 128));
        using var stream = new MemoryStream();
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            image.SaveAsPng(stream);
        else if (name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            image.Save(stream, new GifEncoder());
        else
            image.Save(stream, new JpegEncoder());
        return stream.ToArray();
    }
}
