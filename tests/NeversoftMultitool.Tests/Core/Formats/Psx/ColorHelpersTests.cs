using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public class ColorHelpersTests
{
    [Fact]
    public void Ps1To32Bpp_Magenta_IsFullyTransparent()
    {
        // Magenta (R=31, G=0, B=31) is the transparency key
        ushort magenta = 0x7C1F; // R=31, G=0, B=31
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Ps1To32Bpp(magenta, rgba);

        Assert.Equal(0, rgba[0]); // R
        Assert.Equal(0, rgba[1]); // G
        Assert.Equal(0, rgba[2]); // B
        Assert.Equal(0, rgba[3]); // A = transparent
    }

    [Fact]
    public void Ps1To32Bpp_Black_IsOpaqueBlack()
    {
        ushort black = 0x0000;
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Ps1To32Bpp(black, rgba);

        Assert.Equal(0, rgba[0]);
        Assert.Equal(0, rgba[1]);
        Assert.Equal(0, rgba[2]);
        Assert.Equal(255, rgba[3]); // opaque
    }

    [Fact]
    public void Ps1To32Bpp_PureRed_ConvertsCorrectly()
    {
        // PS1 format: BBBBB_GGGGG_RRRRR (LSB first)
        ushort red = 0x001F; // R=31, G=0, B=0
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Ps1To32Bpp(red, rgba);

        Assert.Equal(255, rgba[0]); // R
        Assert.Equal(0, rgba[1]);   // G
        Assert.Equal(0, rgba[2]);   // B
        Assert.Equal(255, rgba[3]); // A
    }

    [Fact]
    public void Ps1To32Bpp_PureGreen_ConvertsCorrectly()
    {
        ushort green = 0x03E0; // R=0, G=31, B=0
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Ps1To32Bpp(green, rgba);

        Assert.Equal(0, rgba[0]);
        Assert.Equal(255, rgba[1]);
        Assert.Equal(0, rgba[2]);
        Assert.Equal(255, rgba[3]);
    }

    [Fact]
    public void Ps1To32Bpp_PureBlue_ConvertsCorrectly()
    {
        ushort blue = 0x7C00; // R=0, G=0, B=31
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Ps1To32Bpp(blue, rgba);

        Assert.Equal(0, rgba[0]);
        Assert.Equal(0, rgba[1]);
        Assert.Equal(255, rgba[2]);
        Assert.Equal(255, rgba[3]);
    }

    [Theory]
    [InlineData(0u, ColorFormat.Argb1555)]
    [InlineData(1u, ColorFormat.Rgb565)]
    [InlineData(2u, ColorFormat.Argb4444)]
    [InlineData(0xF02u, ColorFormat.Argb4444)]
    public void Get16BppColorFormat_ReturnsCorrectFormat(uint pixelFormat, ColorFormat expected)
    {
        Assert.Equal(expected, ColorHelpers.Get16BppColorFormat(pixelFormat));
    }

    [Fact]
    public void Convert16BppTo32Bpp_Argb1555_AlphaBitSet_IsOpaque()
    {
        // ARGB1555: A=1 R=31 G=0 B=0 → 1_11111_00000_00000 = 0xFC00
        ushort color = 0xFC00;
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Convert16BppTo32Bpp(color, ColorFormat.Argb1555, rgba);

        Assert.Equal(255, rgba[0]); // R
        Assert.Equal(0, rgba[1]);   // G
        Assert.Equal(0, rgba[2]);   // B
        Assert.Equal(255, rgba[3]); // A=1 → 255
    }

    [Fact]
    public void Convert16BppTo32Bpp_Argb1555_AlphaBitClear_IsTransparent()
    {
        // ARGB1555: A=0 R=31 G=0 B=0 → 0_11111_00000_00000 = 0x7C00
        ushort color = 0x7C00;
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Convert16BppTo32Bpp(color, ColorFormat.Argb1555, rgba);

        Assert.Equal(255, rgba[0]); // R
        Assert.Equal(0, rgba[1]);   // G
        Assert.Equal(0, rgba[2]);   // B
        Assert.Equal(0, rgba[3]);   // A=0 → 0
    }

    [Fact]
    public void Convert16BppTo32Bpp_Rgb565_AlwaysOpaque()
    {
        // RGB565: R=31 G=63 B=31 = 0xFFFF (white)
        ushort white = 0xFFFF;
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Convert16BppTo32Bpp(white, ColorFormat.Rgb565, rgba);

        Assert.Equal(255, rgba[0]); // R
        Assert.Equal(255, rgba[1]); // G
        Assert.Equal(255, rgba[2]); // B
        Assert.Equal(255, rgba[3]); // Always opaque
    }

    [Fact]
    public void Convert16BppTo32Bpp_Argb4444_HalfAlpha()
    {
        // ARGB4444: A=7 R=0 G=0 B=0 → 0x7000
        ushort color = 0x7000;
        Span<byte> rgba = stackalloc byte[4];
        ColorHelpers.Convert16BppTo32Bpp(color, ColorFormat.Argb4444, rgba);

        Assert.Equal(0, rgba[0]);
        Assert.Equal(0, rgba[1]);
        Assert.Equal(0, rgba[2]);
        Assert.Equal(7 * 255 / 15, rgba[3]); // ~119
    }
}
