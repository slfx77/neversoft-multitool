using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2;

/// <summary>
///     Tests for version-2 .img.ps2 pixel-region handling (Ps2ImgV2File): non-POT sprites
///     shipping the full (1&lt;&lt;TW)x(1&lt;&lt;TH) VRAM upload buffer must be de-strided
///     (bottom-anchored, matching PS2 bottom-up row storage), tight orig-stride files must
///     be read contiguously, and PSMT8 CLUTs must be used linearly (CSM0) because the
///     engine applies the CSM1 rearrange itself at load time (THUG sprite.cpp
///     setup_reg_and_dma), so extraction must NOT unswizzle them.
/// </summary>
public class Ps2ImgV2DestrideTests
{
    private const uint Psmct32 = 0x00;
    private const uint Psmt8 = 0x13;
    private const uint Psmt4 = 0x14;

    [Fact]
    public void Parse_DimensionsWhoseRgbaBufferCannotFit_FailsBeforeSizeArithmetic()
    {
        var data = BuildImgV2(
            0, 0, Psmct32, Psmct32,
            ushort.MaxValue, ushort.MaxValue, [], []);

        var result = Ps2ImgV2File.Parse(data);

        Assert.False(result.Success);
        Assert.Equal(
            "IMG dimensions 65535x65535 exceed the supported RGBA pixel buffer",
            result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_VramPaddedNonPot_DestridesBottomAnchoredRows()
    {
        // 6x4 image in an 8x8 VRAM buffer (TW=TH=3), PSMT8/PSMCT32.
        // Rows are stored bottom-up and the content sits in the LAST origH rows, so the
        // decoded top-left pixel comes from the LAST buffer row (row 7), column 0.
        var pixels = new byte[8 * 8];
        pixels[7 * 8 + 0] = 42;

        var clut = new byte[256 * 4];
        SetClutEntry(clut, 42, 200, 10, 30);

        var data = BuildImgV2(3, 3, Psmt8, Psmct32, 6, 4, clut, pixels);
        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.Equal(6, tex.Width);
        Assert.Equal(4, tex.Height);
        Assert.NotNull(tex.Pixels);
        AssertRgb(tex.Pixels, 0, 0, 6, 200, 10, 30);
    }

    [Fact]
    public void Parse_OrigStrideNonPot_DoesNotDestride()
    {
        // 6x4 image stored tight (24 bytes = origW*origH exactly, like the shipped font
        // small.img.ps2 whose region matches orig dimensions). No de-stride: the decoded
        // top-left pixel comes from the last tight row (row 3 of 4), column 0.
        var pixels = new byte[6 * 4];
        pixels[3 * 6 + 0] = 42;

        var clut = new byte[256 * 4];
        SetClutEntry(clut, 42, 200, 10, 30);

        var data = BuildImgV2(3, 3, Psmt8, Psmct32, 6, 4, clut, pixels);
        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.Equal(6, tex.Width);
        Assert.Equal(4, tex.Height);
        Assert.NotNull(tex.Pixels);
        AssertRgb(tex.Pixels, 0, 0, 6, 200, 10, 30);
    }

    [Fact]
    public void Parse_VramPaddedPsmt4OddWidth_DestridesNibbles()
    {
        // 5x3 PSMT4 image in an 8x8 VRAM buffer: odd width means tight rows are not
        // byte-aligned (the 121-wide score_small case that crashed the old parser).
        // Decoded top-left pixel = VRAM row 7 (bottom anchor + flip), column 0 =
        // nibble index 56 = low nibble of byte 28.
        var pixels = new byte[8 * 8 / 2];
        pixels[28] = 0x07; // low nibble = palette index 7

        var clut = new byte[16 * 4];
        SetClutEntry(clut, 7, 55, 66, 77);

        var data = BuildImgV2(3, 3, Psmt4, Psmct32, 5, 3, clut, pixels);
        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.Equal(5, tex.Width);
        Assert.Equal(3, tex.Height);
        Assert.NotNull(tex.Pixels);
        AssertRgb(tex.Pixels, 0, 0, 5, 55, 66, 77);
    }

    [Fact]
    public void Parse_Psmt8Clut_IsReadLinearWithoutCsm1Unswizzle()
    {
        // Palette index 8 must resolve to STORED entry 8. A CSM1 unswizzle would swap
        // entry blocks 8-15 and 16-23 and read entry 16's color instead. The engine
        // rearranges the linear file CLUT itself at load (sprite.cpp), so extraction
        // must not: verified visually against THUG mm_building.img.ps2.
        var pixels = new byte[4 * 4];
        Array.Fill(pixels, (byte)8);

        var clut = new byte[256 * 4];
        SetClutEntry(clut, 8, 11, 22, 33);
        SetClutEntry(clut, 16, 99, 88, 77);

        var data = BuildImgV2(2, 2, Psmt8, Psmct32, 4, 4, clut, pixels);
        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.NotNull(tex.Pixels);
        AssertRgb(tex.Pixels, 1, 1, 4, 11, 22, 33);
    }

    /// <summary>
    ///     Builds a synthetic version-2 .img.ps2 file: 32-byte header + CLUT
    ///     (16-byte aligned) + raw pixel data.
    /// </summary>
    private static byte[] BuildImgV2(uint tw, uint th, uint psm, uint cpsm,
        ushort origW, ushort origH, byte[] clut, byte[] pixels)
    {
        var clutEnd = (32 + clut.Length + 15) & ~15;
        var data = new byte[clutEnd + pixels.Length];

        WriteU32(data, 0, 2); // version
        WriteU32(data, 4, 0xABCD1234); // checksum
        WriteU32(data, 8, tw);
        WriteU32(data, 12, th);
        WriteU32(data, 16, psm);
        WriteU32(data, 20, cpsm);
        WriteU32(data, 24, 0); // MXL
        data[28] = (byte)(origW & 0xFF);
        data[29] = (byte)(origW >> 8);
        data[30] = (byte)(origH & 0xFF);
        data[31] = (byte)(origH >> 8);

        clut.CopyTo(data, 32);
        pixels.CopyTo(data, clutEnd);
        return data;
    }

    private static void SetClutEntry(byte[] clut, int index, byte r, byte g, byte b)
    {
        clut[index * 4] = r;
        clut[index * 4 + 1] = g;
        clut[index * 4 + 2] = b;
        clut[index * 4 + 3] = 128; // GS full alpha
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        BitConverter.GetBytes(value).CopyTo(data, offset);
    }

    private static void AssertRgb(byte[] pixels, int x, int y, int width, byte r, byte g, byte b)
    {
        var i = (y * width + x) * 4;
        Assert.Equal(r, pixels[i]);
        Assert.Equal(g, pixels[i + 1]);
        Assert.Equal(b, pixels[i + 2]);
    }
}
