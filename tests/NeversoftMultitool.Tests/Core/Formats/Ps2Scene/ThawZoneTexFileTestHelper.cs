using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

internal static class ThawZoneTexFileTestHelper
{
    internal static void WriteQword(List<byte> bytes, ulong value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    internal static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    internal static void WriteUInt64(byte[] bytes, int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
    }

    internal static ulong BuildTex0(uint tbp, uint tbw, uint psm, int width, int height, uint cbp, uint cpsm)
    {
        return tbp
               | ((ulong)tbw << 14)
               | ((ulong)psm << 20)
               | ((ulong)Log2(width) << 26)
               | ((ulong)Log2(height) << 30)
               | ((ulong)cbp << 37)
               | ((ulong)cpsm << 51)
               | (1ul << 61);
    }

    internal static byte[] BuildCt32Palette(byte r, byte g, byte b, byte a)
    {
        var palette = new byte[16 * 16 * 4];
        palette[0] = r;
        palette[1] = g;
        palette[2] = b;
        palette[3] = a;
        return palette;
    }

    internal static byte[] BuildCt32PaletteLowEntropy(byte r, byte g, byte b, byte a)
    {
        var palette = new byte[0x40];
        palette[0] = r;
        palette[1] = g;
        palette[2] = b;
        palette[3] = a;
        return palette;
    }

    internal static byte[] BuildCt32PaletteWithEntropy(byte r, byte g, byte b, byte a, byte seed)
    {
        var palette = new byte[0x40];
        palette[0] = r;
        palette[1] = g;
        palette[2] = b;
        palette[3] = a;

        for (var i = 4; i < palette.Length; i++)
            palette[i] = (byte)(seed + i * 17);

        return palette;
    }

    internal static byte[] BuildRawCt32Psmt4Palette(
        int rawRedIndex,
        byte redR,
        byte redG,
        byte redB,
        byte redA,
        int rawBlueIndex,
        byte blueR,
        byte blueG,
        byte blueB,
        byte blueA)
    {
        var palette = new byte[16 * 4];
        var redOffset = rawRedIndex * 4;
        palette[redOffset] = redR;
        palette[redOffset + 1] = redG;
        palette[redOffset + 2] = redB;
        palette[redOffset + 3] = redA;

        var blueOffset = rawBlueIndex * 4;
        palette[blueOffset] = blueR;
        palette[blueOffset + 1] = blueG;
        palette[blueOffset + 2] = blueB;
        palette[blueOffset + 3] = blueA;
        return palette;
    }

    internal static byte[] BuildRawCt16Psmt4Palette(int rawRedIndex, ushort red, int rawBlueIndex, ushort blue)
    {
        var palette = new byte[16 * 2];

        var redOffset = rawRedIndex * 2;
        palette[redOffset] = (byte)red;
        palette[redOffset + 1] = (byte)(red >> 8);

        var blueOffset = rawBlueIndex * 2;
        palette[blueOffset] = (byte)blue;
        palette[blueOffset + 1] = (byte)(blue >> 8);

        return palette;
    }

    internal static void AssertSolidColor(Ps2Texture texture, byte r, byte g, byte b, byte a)
    {
        Assert.NotNull(texture.Pixels);
        for (var i = 0; i < texture.Pixels!.Length; i += 4)
        {
            Assert.Equal(r, texture.Pixels[i]);
            Assert.Equal(g, texture.Pixels[i + 1]);
            Assert.Equal(b, texture.Pixels[i + 2]);
            Assert.Equal(a, texture.Pixels[i + 3]);
        }
    }

    internal static void FillPsmt4Block(
        byte[] texData,
        int width,
        int x,
        int y,
        int blockWidth,
        int blockHeight,
        byte index)
    {
        for (var row = y; row < y + blockHeight; row++)
        {
            for (var column = x; column < x + blockWidth; column++)
                WritePsmt4Index(texData, width, column, row, index);
        }
    }

    internal static byte ReadPsmt4Index(byte[] texData, int width, int x, int y)
    {
        var pixelIndex = y * width + x;
        var byteIndex = pixelIndex >> 1;
        return (byte)((pixelIndex & 1) == 0
            ? texData[byteIndex] & 0x0F
            : (texData[byteIndex] >> 4) & 0x0F);
    }

    private static void WritePsmt4Index(byte[] texData, int width, int x, int y, byte index)
    {
        var pixelIndex = y * width + x;
        var byteIndex = pixelIndex >> 1;
        if ((pixelIndex & 1) == 0)
            texData[byteIndex] = (byte)((texData[byteIndex] & 0xF0) | (index & 0x0F));
        else
            texData[byteIndex] = (byte)((texData[byteIndex] & 0x0F) | ((index & 0x0F) << 4));
    }

    private static int Log2(int value)
    {
        var result = 0;
        while (1 << result < value)
            result++;

        return result;
    }
}
