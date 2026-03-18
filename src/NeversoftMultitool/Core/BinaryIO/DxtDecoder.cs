using System.Buffers.Binary;

namespace NeversoftMultitool.Core.BinaryIO;

/// <summary>
///     Decodes DXT1, DXT3, and DXT5 (S3TC) compressed texture blocks to RGBA32.
/// </summary>
public static class DxtDecoder
{
    /// <summary>Decode DXT1 compressed data to RGBA32 (4 bytes/pixel).</summary>
    public static byte[] DecodeDxt1(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;
        var offset = 0;

        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                if (offset + 8 > data.Length) return output;
                DecodeDxt1Block(data.Slice(offset, 8), output, bx * 4, by * 4, width, height, true);
                offset += 8;
            }
        }

        return output;
    }

    /// <summary>Decode DXT3 (explicit alpha) compressed data to RGBA32 (4 bytes/pixel).</summary>
    public static byte[] DecodeDxt3(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;
        var offset = 0;

        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                if (offset + 16 > data.Length) return output;

                // 8 bytes explicit alpha + 8 bytes color block
                DecodeExplicitAlphaBlock(data.Slice(offset, 8), output, bx * 4, by * 4, width, height);
                DecodeDxt1Block(data.Slice(offset + 8, 8), output, bx * 4, by * 4, width, height, false);
                offset += 16;
            }
        }

        return output;
    }

    /// <summary>Decode DXT5 compressed data to RGBA32 (4 bytes/pixel).</summary>
    public static byte[] DecodeDxt5(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;
        var offset = 0;

        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                if (offset + 16 > data.Length) return output;

                // 8 bytes alpha block + 8 bytes color block
                DecodeAlphaBlock(data.Slice(offset, 8), output, bx * 4, by * 4, width, height);
                DecodeDxt1Block(data.Slice(offset + 8, 8), output, bx * 4, by * 4, width, height, false);
                offset += 16;
            }
        }

        return output;
    }

    private static void DecodeDxt1Block(ReadOnlySpan<byte> block, byte[] output,
        int px, int py, int width, int height, bool hasAlpha)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        var lut = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);

        Span<byte> colors = stackalloc byte[16]; // 4 colors × RGBA
        UnpackRgb565(c0, colors);
        colors[3] = 255;
        UnpackRgb565(c1, colors[4..]);
        colors[7] = 255;

        if (c0 > c1)
        {
            // 4-color mode: c2 = 2/3*c0 + 1/3*c1, c3 = 1/3*c0 + 2/3*c1
            colors[8] = (byte)((2 * colors[0] + colors[4] + 1) / 3);
            colors[9] = (byte)((2 * colors[1] + colors[5] + 1) / 3);
            colors[10] = (byte)((2 * colors[2] + colors[6] + 1) / 3);
            colors[11] = 255;
            colors[12] = (byte)((colors[0] + 2 * colors[4] + 1) / 3);
            colors[13] = (byte)((colors[1] + 2 * colors[5] + 1) / 3);
            colors[14] = (byte)((colors[2] + 2 * colors[6] + 1) / 3);
            colors[15] = 255;
        }
        else
        {
            // 3-color + transparent: c2 = 1/2*c0 + 1/2*c1, c3 = transparent black
            colors[8] = (byte)((colors[0] + colors[4] + 1) / 2);
            colors[9] = (byte)((colors[1] + colors[5] + 1) / 2);
            colors[10] = (byte)((colors[2] + colors[6] + 1) / 2);
            colors[11] = 255;
            colors[12] = 0;
            colors[13] = 0;
            colors[14] = 0;
            colors[15] = hasAlpha ? (byte)0 : (byte)255;
        }

        for (var y = 0; y < 4; y++)
        {
            var oy = py + y;
            if (oy >= height) break;
            for (var x = 0; x < 4; x++)
            {
                var ox = px + x;
                if (ox >= width) continue;
                var idx = (int)((lut >> (2 * (y * 4 + x))) & 0x03);
                var dest = (oy * width + ox) * 4;
                output[dest] = colors[idx * 4];
                output[dest + 1] = colors[idx * 4 + 1];
                output[dest + 2] = colors[idx * 4 + 2];
                if (hasAlpha)
                    output[dest + 3] = colors[idx * 4 + 3];
                // DXT5: alpha written separately by DecodeAlphaBlock
            }
        }
    }

    private static void DecodeExplicitAlphaBlock(ReadOnlySpan<byte> block, byte[] output,
        int px, int py, int width, int height)
    {
        // DXT3: 8 bytes = 16 pixels × 4 bits each (explicit alpha values)
        for (var y = 0; y < 4; y++)
        {
            var oy = py + y;
            if (oy >= height) break;
            // Each row = 2 bytes = 8 nibbles for 4 pixels
            var rowBits = BinaryPrimitives.ReadUInt16LittleEndian(block[(y * 2)..]);
            for (var x = 0; x < 4; x++)
            {
                var ox = px + x;
                if (ox >= width) continue;
                var alpha4 = (rowBits >> (x * 4)) & 0x0F;
                var dest = (oy * width + ox) * 4;
                output[dest + 3] = (byte)((alpha4 << 4) | alpha4); // expand 4-bit to 8-bit
            }
        }
    }

    private static void DecodeAlphaBlock(ReadOnlySpan<byte> block, byte[] output,
        int px, int py, int width, int height)
    {
        var a0 = block[0];
        var a1 = block[1];

        // 48-bit alpha LUT (6 bytes, 3 bits per pixel for 16 pixels)
        var bits = block[2] | ((ulong)block[3] << 8) | ((ulong)block[4] << 16) |
                   ((ulong)block[5] << 24) | ((ulong)block[6] << 32) | ((ulong)block[7] << 40);

        Span<byte> alphas = stackalloc byte[8];
        alphas[0] = a0;
        alphas[1] = a1;

        if (a0 > a1)
        {
            alphas[2] = (byte)((6 * a0 + 1 * a1 + 3) / 7);
            alphas[3] = (byte)((5 * a0 + 2 * a1 + 3) / 7);
            alphas[4] = (byte)((4 * a0 + 3 * a1 + 3) / 7);
            alphas[5] = (byte)((3 * a0 + 4 * a1 + 3) / 7);
            alphas[6] = (byte)((2 * a0 + 5 * a1 + 3) / 7);
            alphas[7] = (byte)((1 * a0 + 6 * a1 + 3) / 7);
        }
        else
        {
            alphas[2] = (byte)((4 * a0 + 1 * a1 + 2) / 5);
            alphas[3] = (byte)((3 * a0 + 2 * a1 + 2) / 5);
            alphas[4] = (byte)((2 * a0 + 3 * a1 + 2) / 5);
            alphas[5] = (byte)((1 * a0 + 4 * a1 + 2) / 5);
            alphas[6] = 0;
            alphas[7] = 255;
        }

        for (var y = 0; y < 4; y++)
        {
            var oy = py + y;
            if (oy >= height) break;
            for (var x = 0; x < 4; x++)
            {
                var ox = px + x;
                if (ox >= width) continue;
                var bitIndex = 3 * (y * 4 + x);
                var idx = (int)((bits >> bitIndex) & 0x07);
                var dest = (oy * width + ox) * 4;
                output[dest + 3] = alphas[idx];
            }
        }
    }

    private static void UnpackRgb565(ushort color, Span<byte> rgba)
    {
        var r = (color >> 11) & 0x1F;
        var g = (color >> 5) & 0x3F;
        var b = color & 0x1F;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
    }
}
