using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class ThawImgFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2006-2-6, PC - Final)";

    [Fact]
    public void IsThawImg_EmptyData_ReturnsFalse()
    {
        Assert.False(ThawImgFile.IsThawImg([]));
    }

    [Fact]
    public void Parse_SyntheticBgra32Img_FlipsToTopDown()
    {
        var data = BuildBgra32Img(2, 2,
        [
            // Stored bottom-up: bottom row first, then top row.
            255, 0, 0, 255, // blue in BGRA
            255, 255, 255, 255, // white
            0, 0, 255, 255, // red in BGRA
            0, 255, 0, 255 // green
        ]);

        var result = ThawImgFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.Equal(2, tex.Width);
        Assert.Equal(2, tex.Height);
        Assert.NotNull(tex.Pixels);
        Assert.Equal(16, tex.Pixels!.Length);

        Assert.Equal([255, 0, 0, 255], tex.Pixels[..4]); // top-left = red
        Assert.Equal([0, 255, 0, 255], tex.Pixels[4..8]); // top-right = green
        Assert.Equal([0, 0, 255, 255], tex.Pixels[8..12]); // bottom-left = blue
        Assert.Equal([255, 255, 255, 255], tex.Pixels[12..]); // bottom-right = white
    }

    [Fact]
    public void Parse_SyntheticPaletted4Img_DecodesPalette()
    {
        var palette = new byte[16 * 4];
        WritePaletteColor(palette, 1, 255, 0, 0, 255);
        WritePaletteColor(palette, 2, 0, 255, 0, 255);
        WritePaletteColor(palette, 3, 0, 0, 255, 255);
        WritePaletteColor(palette, 4, 255, 255, 0, 255);
        WritePaletteColor(palette, 5, 255, 0, 255, 255);
        WritePaletteColor(palette, 6, 0, 255, 255, 255);
        WritePaletteColor(palette, 7, 128, 128, 128, 255);
        WritePaletteColor(palette, 8, 255, 255, 255, 255);

        var data = BuildPaletted4Img(4, 2, palette,
        [
            // Stored bottom-up. Row bytes: p0 low nibble, p1 high nibble.
            0x65, 0x87, // bottom row: 5,6,7,8
            0x21, 0x43 // top row: 1,2,3,4
        ]);

        var result = ThawImgFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.Equal(4, tex.Width);
        Assert.Equal(2, tex.Height);
        Assert.NotNull(tex.Pixels);
        Assert.Equal(32, tex.Pixels!.Length);

        Assert.Equal([255, 0, 0, 255], tex.Pixels[..4]); // 1
        Assert.Equal([0, 255, 0, 255], tex.Pixels[4..8]); // 2
        Assert.Equal([0, 0, 255, 255], tex.Pixels[8..12]); // 3
        Assert.Equal([255, 255, 0, 255], tex.Pixels[12..16]); // 4
        Assert.Equal([255, 0, 255, 255], tex.Pixels[16..20]); // 5
        Assert.Equal([0, 255, 255, 255], tex.Pixels[20..24]); // 6
        Assert.Equal([128, 128, 128, 255], tex.Pixels[24..28]); // 7
        Assert.Equal([255, 255, 255, 255], tex.Pixels[28..32]); // 8
    }

    [Fact]
    public void IsThawImg_RealSample_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "levelposter_hollywood.img.wpc");
        Assert.SkipWhen(file is null, "levelposter_hollywood.img.wpc not found");

        var data = File.ReadAllBytes(file);
        Assert.True(ThawImgFile.IsThawImg(data));
    }

    [Fact]
    public void Parse_LevelPosterHollywood_Succeeds()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "levelposter_hollywood.img.wpc");
        Assert.SkipWhen(file is null, "levelposter_hollywood.img.wpc not found");

        var result = ThawImgFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.Equal(128, tex.Width);
        Assert.Equal(128, tex.Height);
        Assert.NotNull(tex.Pixels);
    }

    [Fact]
    public void Parse_SpazznotchPaletted_Succeeds()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "spazznotch.img.wpc");
        Assert.SkipWhen(file is null, "spazznotch.img.wpc not found");

        var result = ThawImgFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.Equal(2, tex.Width);
        Assert.Equal(32, tex.Height);
        Assert.NotNull(tex.Pixels);
    }

    private static byte[] BuildBgra32Img(int width, int height, byte[] bgraPixels)
    {
        var data = new byte[28 + bgraPixels.Length];
        WriteHeader(data, width, height, 1, 32, 0, 0);
        BitConverter.GetBytes((ushort)(width * 4)).CopyTo(data, 24);
        BitConverter.GetBytes((ushort)height).CopyTo(data, 26);
        Buffer.BlockCopy(bgraPixels, 0, data, 28, bgraPixels.Length);
        return data;
    }

    private static byte[] BuildPaletted4Img(int width, int height, byte[] palette, byte[] indexData)
    {
        var data = new byte[28 + 4 + palette.Length + 4 + indexData.Length];
        WriteHeader(data, width, height, 1, 4, 0, 32);
        BitConverter.GetBytes((uint)(palette.Length / 4)).CopyTo(data, 24);
        Buffer.BlockCopy(palette, 0, data, 28, palette.Length);

        var mipHeaderOffset = 28 + palette.Length;
        BitConverter.GetBytes((ushort)(width / 2)).CopyTo(data, mipHeaderOffset);
        BitConverter.GetBytes((ushort)height).CopyTo(data, mipHeaderOffset + 2);
        Buffer.BlockCopy(indexData, 0, data, mipHeaderOffset + 4, indexData.Length);
        return data;
    }

    private static void WriteHeader(byte[] data, int width, int height,
        byte mipCount, byte texelDepth, byte compression, byte paletteDepth)
    {
        BitConverter.GetBytes(0xABADD00Du).CopyTo(data, 0);
        data[4] = 2;
        data[5] = 0;
        BitConverter.GetBytes((ushort)0x14).CopyTo(data, 6);
        BitConverter.GetBytes((uint)0).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)width).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)height).CopyTo(data, 14);
        BitConverter.GetBytes((ushort)width).CopyTo(data, 16);
        BitConverter.GetBytes((ushort)height).CopyTo(data, 18);
        data[20] = mipCount;
        data[21] = texelDepth;
        data[22] = compression;
        data[23] = paletteDepth;
    }

    private static void WritePaletteColor(byte[] palette, int index, byte r, byte g, byte b, byte a)
    {
        var offset = index * 4;
        palette[offset] = r;
        palette[offset + 1] = g;
        palette[offset + 2] = b;
        palette[offset + 3] = a;
    }
}