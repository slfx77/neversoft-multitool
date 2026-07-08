namespace NeversoftMultitool.Core.Formats.Texture.Ngc;

/// <summary>
///     Decodes GameCube GX_TF_RGBA8 texel data: 4×4 texel tiles stored as two
///     32-byte planes per tile — AR byte pairs followed by GB byte pairs.
/// </summary>
internal static class NgcTexRgba8Decoder
{
    public static byte[] DecodeToRgba(ReadOnlySpan<byte> data, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var pixels = new byte[width * height * 4];
        var offset = 0;
        for (var tileY = 0; tileY < height; tileY += 4)
        {
            for (var tileX = 0; tileX < width; tileX += 4)
            {
                if (offset + 64 > data.Length)
                    throw new InvalidDataException("RGBA8 data truncated");

                for (var row = 0; row < 4; row++)
                {
                    for (var column = 0; column < 4; column++)
                    {
                        var y = tileY + row;
                        var x = tileX + column;
                        if (y >= height || x >= width)
                            continue;
                        var i = (row * 4 + column) * 2;
                        var dst = (y * width + x) * 4;
                        pixels[dst] = data[offset + i + 1];
                        pixels[dst + 1] = data[offset + 32 + i];
                        pixels[dst + 2] = data[offset + 32 + i + 1];
                        pixels[dst + 3] = data[offset + i];
                    }
                }

                offset += 64;
            }
        }

        return pixels;
    }
}
