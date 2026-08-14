using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Texture.Ngc;

internal static class NgcTexCmprDecoder
{
    public static byte[] DecodeToRgba(ReadOnlySpan<byte> data, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var tileColumns = ((long)width + 7) / 8;
        var tileRows = ((long)height + 7) / 8;
        var paddedWidthLong = tileColumns * 8;
        var paddedHeightLong = tileRows * 8;
        var paddedPixelCount = paddedWidthLong * paddedHeightLong;
        if (paddedPixelCount > Array.MaxLength / 4L)
        {
            throw new InvalidDataException(
                $"CMPR dimensions {width}x{height} exceed the runtime array limit");
        }

        var expectedSize = paddedPixelCount / 2;

        if (data.Length < expectedSize)
        {
            throw new InvalidDataException(
                $"CMPR data too small: expected at least {expectedSize} bytes, found {data.Length}.");
        }

        var paddedWidth = (int)paddedWidthLong;
        var paddedHeight = (int)paddedHeightLong;
        var paddedPixels = new byte[(int)(paddedPixelCount * 4)];
        var offset = 0;

        for (var y = 0; y < paddedHeight; y += 8)
        {
            for (var x = 0; x < paddedWidth; x += 8)
            {
                for (var subY = 0; subY < 8; subY += 4)
                {
                    for (var subX = 0; subX < 8; subX += 4)
                    {
                        DecodeSubBlock(
                            data.Slice(offset, 8),
                            paddedPixels,
                            paddedWidth,
                            x + subX,
                            y + subY);
                        offset += 8;
                    }
                }
            }
        }

        if (paddedWidth == width && paddedHeight == height)
        {
            return paddedPixels;
        }

        var pixelCount = (long)width * height;
        var croppedPixels = new byte[(int)(pixelCount * 4)];
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            var sourceOffset = y * paddedWidth * 4;
            var destinationOffset = y * rowBytes;
            Buffer.BlockCopy(paddedPixels, sourceOffset, croppedPixels, destinationOffset, rowBytes);
        }

        return croppedPixels;
    }

    private static void DecodeSubBlock(
        ReadOnlySpan<byte> block,
        byte[] output,
        int imageWidth,
        int startX,
        int startY)
    {
        var color0 = BinaryPrimitives.ReadUInt16BigEndian(block);
        var color1 = BinaryPrimitives.ReadUInt16BigEndian(block[2..]);
        var palette = BuildPalette(color0, color1);

        for (var row = 0; row < 4; row++)
        {
            var selectors = block[4 + row];
            for (var column = 0; column < 4; column++)
            {
                var paletteIndex = (selectors >> (6 - column * 2)) & 0x03;
                var pixelOffset = ((startY + row) * imageWidth + startX + column) * 4;
                var color = palette[paletteIndex];
                output[pixelOffset] = color.R;
                output[pixelOffset + 1] = color.G;
                output[pixelOffset + 2] = color.B;
                output[pixelOffset + 3] = color.A;
            }
        }
    }

    private static Rgba32[] BuildPalette(ushort color0, ushort color1)
    {
        var palette = new Rgba32[4];
        palette[0] = DecodeRgb565(color0);
        palette[1] = DecodeRgb565(color1);

        if (color0 > color1)
        {
            palette[2] = Interpolate(palette[0], palette[1], 2, 1);
            palette[3] = Interpolate(palette[0], palette[1], 1, 2);
        }
        else
        {
            palette[2] = Average(palette[0], palette[1]);
            palette[3] = new Rgba32(0, 0, 0, 0);
        }

        return palette;
    }

    private static Rgba32 DecodeRgb565(ushort value)
    {
        var r5 = (value >> 11) & 0x1F;
        var g6 = (value >> 5) & 0x3F;
        var b5 = value & 0x1F;
        var r = (byte)((r5 << 3) | (r5 >> 2));
        var g = (byte)((g6 << 2) | (g6 >> 4));
        var b = (byte)((b5 << 3) | (b5 >> 2));
        return new Rgba32(r, g, b, 0xFF);
    }

    private static Rgba32 Interpolate(Rgba32 a, Rgba32 b, int weightA, int weightB)
    {
        var divisor = weightA + weightB;
        return new Rgba32(
            (byte)((a.R * weightA + b.R * weightB) / divisor),
            (byte)((a.G * weightA + b.G * weightB) / divisor),
            (byte)((a.B * weightA + b.B * weightB) / divisor),
            (byte)((a.A * weightA + b.A * weightB) / divisor));
    }

    private static Rgba32 Average(Rgba32 a, Rgba32 b)
    {
        return new Rgba32(
            (byte)((a.R + b.R) / 2),
            (byte)((a.G + b.G) / 2),
            (byte)((a.B + b.B) / 2),
            (byte)((a.A + b.A) / 2));
    }
    private readonly record struct Rgba32(byte R, byte G, byte B, byte A);
}
