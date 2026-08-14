using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Texture.Ngc;

/// <summary>
///     Decodes GameCube GX_TF_C8 texel data: 8×4 texel tiles of 8-bit palette
///     indices followed by a 256-entry big-endian RGB5A3 palette.
/// </summary>
internal static class NgcTexC8Decoder
{
    public const int PaletteBytes = 256 * 2;

    public static byte[] DecodeToRgba(ReadOnlySpan<byte> data, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var pixelCount = (long)width * height;
        if (pixelCount > Array.MaxLength / 4L)
        {
            throw new InvalidDataException(
                $"C8 dimensions {width}x{height} exceed the runtime array limit");
        }

        var tileColumns = ((long)width + 7) / 8;
        var tileRows = ((long)height + 3) / 4;
        var indexBytes = tileColumns * tileRows * 32;
        if (indexBytes + PaletteBytes > data.Length)
            throw new InvalidDataException("C8 data truncated");

        var palette = new byte[256 * 4];
        var paletteData = data.Slice((int)indexBytes, PaletteBytes);
        for (var i = 0; i < 256; i++)
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(paletteData[(i * 2)..]);
            DecodeRgb5A3(value, palette.AsSpan(i * 4, 4));
        }

        var pixels = new byte[(int)(pixelCount * 4)];
        var offset = 0;
        for (var tileY = 0; tileY < height; tileY += 4)
        {
            for (var tileX = 0; tileX < width; tileX += 8)
            {
                for (var row = 0; row < 4; row++)
                {
                    for (var column = 0; column < 8; column++)
                    {
                        var index = data[offset++];
                        var y = tileY + row;
                        var x = tileX + column;
                        if (y >= height || x >= width)
                            continue;
                        palette.AsSpan(index * 4, 4).CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
                    }
                }
            }
        }

        return pixels;
    }

    private static void DecodeRgb5A3(ushort value, Span<byte> rgba)
    {
        if ((value & 0x8000) != 0)
        {
            var r = (value >> 10) & 0x1F;
            var g = (value >> 5) & 0x1F;
            var b = value & 0x1F;
            rgba[0] = (byte)((r << 3) | (r >> 2));
            rgba[1] = (byte)((g << 3) | (g >> 2));
            rgba[2] = (byte)((b << 3) | (b >> 2));
            rgba[3] = 255;
        }
        else
        {
            var a = (value >> 12) & 0x7;
            var r = (value >> 8) & 0xF;
            var g = (value >> 4) & 0xF;
            var b = value & 0xF;
            rgba[0] = (byte)(r * 17);
            rgba[1] = (byte)(g * 17);
            rgba[2] = (byte)(b * 17);
            rgba[3] = (byte)(a * 255 / 7);
        }
    }
}
