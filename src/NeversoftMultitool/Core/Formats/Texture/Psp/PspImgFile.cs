using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Texture.Psp;

/// <summary>
///     Parses THUG2 Remix PSP single-texture IMG files (.img.psp) — the v2 IMG
///     family re-encoded for the PSP GE. Same 32-byte header as the PS2 v2
///     layout, discriminated by the constant 0x0012FC7C at +4 (present in all
///     4,515 Remix files, absent from all 12,558 PS2 v2 files measured).
///     Differences from PS2 v2, each proven by a pixel-exact sweep against the
///     PS2/Xbox siblings (926/926 comparable twins exact, plus the three
///     VRAM-padded swizzled files exact against their Xbox twins):
///     rows are TOP-DOWN (PS2 stores bottom-up); the flag word at +24 selects
///     PSP GE swizzle (0x00100000, 16-byte × 8-row blocks) rather than the PS2
///     GS layout; CLUTs are linear with FULL-RANGE 0–255 alpha (every one of
///     67,306 compared entries is exactly double its PS2 0–128 sibling); and a
///     pixel region sized to the POT VRAM dims rather than the padded art dims
///     carries the art as the LAST tight-sized bytes (after unswizzling for the
///     swizzled class) — the PSP generalization of the PS2 bottom-anchor.
///     The size-identity classification is exhaustive and unambiguous over the
///     corpus: 2,661 files match both identities with identical strides, 103
///     padded-only, 6 VRAM-only, 0 neither.
/// </summary>
public static class PspImgFile
{
    /// <summary>Constant at header +4 in every Remix PSP IMG; never present in PS2 v2 files.</summary>
    public const uint PspBuildWord = 0x0012FC7C;

    private const uint GeSwizzledFlag = 0x00100000;
    private const uint PsmCt32 = 0x00;
    private const uint PsmT8 = 0x13;
    private const uint PsmT4 = 0x14;

    /// <summary>
    ///     Returns true when the bytes carry the PSP v2 IMG discriminator
    ///     (version 2 + the PSP build word at +4).
    /// </summary>
    public static bool IsPspImg(ReadOnlySpan<byte> data)
    {
        return data.Length >= 32
               && BinaryPrimitives.ReadUInt32LittleEndian(data) == 2
               && BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == PspBuildWord;
    }

    public static Ps2TexResult Parse(byte[] data)
    {
        if (data.Length < 32)
            return Ps2TexResult.Fail("PSP IMG header is truncated");

        var version = BitConverter.ToUInt32(data, 0);
        var buildWord = BitConverter.ToUInt32(data, 4);
        if (version != 2 || buildWord != PspBuildWord)
            return Ps2TexResult.Fail($"Not a PSP v2 IMG (version {version}, word 0x{buildWord:X8})");

        var tw = (int)BitConverter.ToUInt32(data, 8);
        var th = (int)BitConverter.ToUInt32(data, 12);
        var psm = BitConverter.ToUInt32(data, 16);
        var cpsm = BitConverter.ToUInt32(data, 20);
        var flags = BitConverter.ToUInt32(data, 24);
        int width = BitConverter.ToUInt16(data, 28);
        int height = BitConverter.ToUInt16(data, 30);
        if (width == 0) width = 1 << tw;
        if (height == 0) height = 1 << th;
        if (tw > 12 || th > 12 || width <= 0 || height <= 0)
            return Ps2TexResult.Fail($"Implausible PSP IMG dimensions {width}x{height} (tw={tw}, th={th})");

        var bpp = psm switch
        {
            PsmCt32 => 32,
            PsmT8 => 8,
            PsmT4 => 4,
            _ => 0
        };
        if (bpp == 0)
            return Ps2TexResult.Fail($"Unsupported PSP IMG pixel format 0x{psm:X}");

        // Palette: linear entries, full-range alpha (no PS2 0x80-opaque scaling).
        byte[]? palette = null;
        var pixelOffset = 32;
        if (psm is PsmT8 or PsmT4)
        {
            var entries = psm == PsmT8 ? 256 : 16;
            var clutBytes = entries * (cpsm == PsmCt32 ? 4 : 2);
            if (data.Length < 32 + clutBytes)
                return Ps2TexResult.Fail("PSP IMG palette is truncated");

            palette = new byte[entries * 4];
            if (cpsm == PsmCt32)
            {
                Array.Copy(data, 32, palette, 0, clutBytes);
            }
            else if (cpsm == 0x02)
            {
                for (var i = 0; i < entries; i++)
                {
                    var v = BitConverter.ToUInt16(data, 32 + i * 2);
                    palette[i * 4] = Expand5(v & 0x1F);
                    palette[i * 4 + 1] = Expand5((v >> 5) & 0x1F);
                    palette[i * 4 + 2] = Expand5((v >> 10) & 0x1F);
                    palette[i * 4 + 3] = (byte)((v & 0x8000) != 0 ? 255 : 0);
                }
            }
            else
            {
                return Ps2TexResult.Fail($"Unsupported PSP IMG palette format 0x{cpsm:X}");
            }

            pixelOffset = Align(32 + clutBytes, 16);
        }

        if (data.Length < pixelOffset)
            return Ps2TexResult.Fail("PSP IMG pixel region is missing");
        var available = data.Length - pixelOffset;

        var tightRow = (width * bpp + 7) / 8;
        var tightBytes = tightRow * height;
        var vramRow = (1 << tw) * bpp / 8;
        var vramBytes = vramRow * (1 << th);

        byte[] tightPixels;
        if (flags == GeSwizzledFlag)
        {
            var paddedStride = Align(tightRow, 16);
            var paddedRows = Align(height, 8);
            if (available == paddedStride * paddedRows)
            {
                // Art at the top-left of the padded buffer, one padded row per art row.
                var linear = GeUnswizzle(data.AsSpan(pixelOffset, available), paddedStride, paddedRows);
                tightPixels = ExtractRows(linear, paddedStride, tightRow, height, 0);
            }
            else if (vramRow >= 16 && th >= 3 && available == vramBytes)
            {
                // POT VRAM buffer: unswizzle at the VRAM stride, art = last tight bytes.
                var linear = GeUnswizzle(data.AsSpan(pixelOffset, available), vramRow, 1 << th);
                tightPixels = linear[^tightBytes..];
            }
            else
            {
                return Ps2TexResult.Fail(
                    $"PSP IMG swizzled pixel region ({available} bytes) matches neither the padded " +
                    $"({paddedStride * paddedRows}) nor the VRAM ({vramBytes}) identity");
            }
        }
        else if (flags == 0)
        {
            if (available == tightBytes)
            {
                tightPixels = data[pixelOffset..];
            }
            else if (available == vramBytes && vramBytes > tightBytes)
            {
                tightPixels = data[^tightBytes..];
            }
            else
            {
                return Ps2TexResult.Fail(
                    $"PSP IMG linear pixel region ({available} bytes) matches neither the tight " +
                    $"({tightBytes}) nor the VRAM ({vramBytes}) identity");
            }
        }
        else
        {
            return Ps2TexResult.Fail($"Unknown PSP IMG layout flags 0x{flags:X8}");
        }

        // Rows are stored top-down; PSMT4 packs the left pixel in the low nibble.
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * tightRow;
            for (var x = 0; x < width; x++)
            {
                var di = (y * width + x) * 4;
                switch (psm)
                {
                    case PsmT8:
                        Array.Copy(palette!, tightPixels[rowStart + x] * 4, rgba, di, 4);
                        break;
                    case PsmT4:
                        var b = tightPixels[rowStart + (x >> 1)];
                        var index = (x & 1) == 0 ? b & 0xF : (b >> 4) & 0xF;
                        Array.Copy(palette!, index * 4, rgba, di, 4);
                        break;
                    default: // PSMCT32: RGBA with full-range alpha, copied verbatim.
                        Array.Copy(tightPixels, rowStart + x * 4, rgba, di, 4);
                        break;
                }
            }
        }

        return new Ps2TexResult([new Ps2Texture(0, width, height, psm, cpsm, rgba)]);
    }

    private static byte Expand5(int c)
    {
        return (byte)((c << 3) | (c >> 2));
    }

    private static int Align(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>
    ///     PSP GE unswizzle: the buffer is tiled in 16-byte-wide × 8-row blocks,
    ///     stored block-row-major with the 8 16-byte slices of a block contiguous.
    /// </summary>
    private static byte[] GeUnswizzle(ReadOnlySpan<byte> swizzled, int stride, int rows)
    {
        var output = new byte[stride * rows];
        var source = 0;
        for (var blockY = 0; blockY < rows; blockY += 8)
        for (var blockX = 0; blockX < stride; blockX += 16)
        {
            for (var rowInBlock = 0; rowInBlock < 8; rowInBlock++)
            {
                swizzled.Slice(source, 16).CopyTo(output.AsSpan((blockY + rowInBlock) * stride + blockX, 16));
                source += 16;
            }
        }

        return output;
    }

    private static byte[] ExtractRows(byte[] buffer, int stride, int rowBytes, int rows, int firstRow)
    {
        var output = new byte[rowBytes * rows];
        for (var y = 0; y < rows; y++)
            Array.Copy(buffer, (firstRow + y) * stride, output, y * rowBytes, rowBytes);
        return output;
    }
}
