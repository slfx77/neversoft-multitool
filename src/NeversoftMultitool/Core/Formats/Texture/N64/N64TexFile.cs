using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Texture.N64;

/// <summary>
///     N64 texture decoding for the Edge of Reality ports (THPS1/2/3 +
///     Spider-Man), RE'd 2026-08-05 against the PS1 builds' art (the N64
///     controller-glyph and deck-art records render pixel-faithful; the
///     s2ddi02t "hue" mystery was authored variant art, tagged by a hex
///     recolor id in the name field). Two record kinds ship in carved .z64
///     trees:
///
///     DICTIONARY RECORD (.tex.n64, the global texture banks): 32-byte
///     NUL-padded name + BE u16 width/height at +0x20 + BE u16 format word at
///     +0x26 — high byte = G_IM_FMT (0 RGBA, 2 CI, 3 IA, 4 I), low byte =
///     bits per texel (0x04/0x08/0x10, with 0x14 also meaning 16) — + BE u16
///     dataSize at +0x2A + pixel data at +0x40. Rows are padded to whole
///     32-bit words (the source of the corpus's "odd" bytes-per-pixel
///     ratios); dataSize beyond stride*height is the mip chain (the top
///     level is exported). ODD ROWS store the two 32-bit halves of each
///     64-bit TMEM word SWAPPED (texel x ^= 32/bpp) — the RDP interleave
///     baked into storage. 16-bit texels and CI4 palette entries are
///     LITTLE-endian u16 RGBA5551 (R bits 15-11, A bit 0): PS1-era tooling
///     wrote LE fields into an otherwise BE container, the same per-field
///     pattern as the ports' TRG and SFX banks. The CI4 palette (16 entries,
///     32 bytes) sits directly after dataSize; IA8 = I4+A4 nibbles;
///     IA4 = I3+A1; I4/I8 export as opaque grayscale.
///
///     IMAGE RECORD (.img.n64, fullscreen CI8 banks): a 3-slot sub-file
///     table {28-byte header (BE magic 0x00080410, LE constant 3, BE
///     width/height/paletteColors/stride/0), palette of BIG-endian u16
///     RGBA5551, linear top-down CI8 pixels}. A different writer from the
///     dictionary: BE palette, no row swizzle.
/// </summary>
public static class N64TexFile
{
    private const int DictHeaderSize = 0x40;
    private const uint ImageMagic = 0x00080410;

    public sealed record N64Texture(string? Name, int Width, int Height, string Format, byte[] Rgba);

    public static bool IsN64Texture(ReadOnlySpan<byte> data)
    {
        return IsImageRecord(data) || IsDictionaryRecord(data);
    }

    public static N64Texture Decode(byte[] data)
    {
        if (IsImageRecord(data))
            return DecodeImageRecord(data);
        if (IsDictionaryRecord(data))
            return DecodeDictionaryRecord(data);
        throw new InvalidDataException("Not an N64 texture record");
    }

    /// <summary>
    ///     Decodes to {stem}.png. The stem comes from the input FILE name
    ///     (already uniqued by the carver) — re-deriving it from the embedded
    ///     name would collapse the carver's ~slot dedupes into overwrites.
    /// </summary>
    public static string ConvertToPng(string inputPath, string outputDir)
    {
        var data = File.ReadAllBytes(inputPath);
        var texture = Decode(data);
        var stem = Path.GetFileName(inputPath).Split('.')[0];
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"{stem}.png");
        BinaryIO.ImageWriter.WritePng(path, texture.Width, texture.Height, texture.Rgba);
        return path;
    }

    // ---- dictionary records -------------------------------------------------

    public static bool IsDictionaryRecord(ReadOnlySpan<byte> data)
    {
        if (data.Length < DictHeaderSize)
            return false;
        var nul = data[..32].IndexOf((byte)0);
        if (nul < 2)
            return false;
        for (var i = 0; i < nul; i++)
        {
            if (data[i] < 0x20 || data[i] > 0x7E)
                return false;
        }

        var width = BinaryPrimitives.ReadUInt16BigEndian(data[0x20..]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(data[0x22..]);
        if (width is < 1 or > 1024 || height is < 1 or > 1024)
            return false;

        var dataSize = BinaryPrimitives.ReadUInt16BigEndian(data[0x2A..]);
        return TryGetPixelLayout(data, width, height, out _, out _)
               && DictHeaderSize + dataSize <= data.Length;
    }

    public static N64Texture DecodeDictionaryRecord(byte[] data)
    {
        var span = data.AsSpan();
        var nul = span[..32].IndexOf((byte)0);
        var name = nul > 0 ? Encoding.ASCII.GetString(data, 0, nul) : null;
        var width = BinaryPrimitives.ReadUInt16BigEndian(span[0x20..]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(span[0x22..]);
        var dataSize = BinaryPrimitives.ReadUInt16BigEndian(span[0x2A..]);
        if (!TryGetPixelLayout(span, width, height, out var format, out var bitsPerTexel))
        {
            throw new InvalidDataException(
                $"Unknown N64 texture format word 0x{BinaryPrimitives.ReadUInt16BigEndian(span[0x26..]):X4}");
        }

        // Rows pad to whole 64-bit TMEM words (verified against the PS1
        // original of a width-24 CI4 record: 32-bit padding scrambles it,
        // 8-byte padding reproduces the art) — dataSize beyond stride*height
        // is the mip chain.
        var texelsPerWord = 32 / bitsPerTexel;
        var strideBytes = ((width * bitsPerTexel + 7) / 8 + 7) & ~7;
        if (DictHeaderSize + strideBytes * height > data.Length
            || strideBytes * height > dataSize)
        {
            throw new InvalidDataException(
                $"N64 texture {name}: {width}x{height}@{bitsPerTexel}bpp exceeds dataSize {dataSize}");
        }

        var pixels = span.Slice(DictHeaderSize, strideBytes * height);
        var palette = format == 2
            ? ReadPalette(span, DictHeaderSize + dataSize, littleEndian: true)
            : null;

        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * strideBytes, strideBytes);
            // Odd rows swap the two 32-bit halves of each 64-bit word; the
            // 8-byte stride guarantees the partner texel is always in-row.
            var swap = (y & 1) != 0 ? texelsPerWord : 0;
            for (var x = 0; x < width; x++)
            {
                var tx = x ^ swap;
                var (r, g, b, a) = DecodeTexel(row, tx, format, bitsPerTexel, palette);
                var o = (y * width + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = a;
            }
        }

        return new N64Texture(name, width, height, FormatName(format, bitsPerTexel), rgba);
    }

    private static bool TryGetPixelLayout(ReadOnlySpan<byte> data, int width, int height, out int format, out int bitsPerTexel)
    {
        var word = BinaryPrimitives.ReadUInt16BigEndian(data[0x26..]);
        format = word >> 8;
        bitsPerTexel = (word & 0xFF) >= 0x10 ? 16 : word & 0xFF;
        _ = width;
        _ = height;
        return (format, bitsPerTexel) is (0, 16) or (2, 4) or (3, 4) or (3, 8) or (4, 4) or (4, 8);
    }

    private static (byte R, byte G, byte B, byte A) DecodeTexel(
        ReadOnlySpan<byte> row,
        int x,
        int format,
        int bitsPerTexel,
        byte[]? palette)
    {
        switch (format, bitsPerTexel)
        {
            case (0, 16): // RGBA16, LE u16 5551
            {
                var v = (ushort)(row[x * 2] | (row[x * 2 + 1] << 8));
                return Rgba5551(v);
            }
            case (2, 4): // CI4 against the 16-entry palette
            {
                var index = Nibble(row, x);
                var v = (ushort)(palette![index * 2] | (palette[index * 2 + 1] << 8));
                return Rgba5551(v);
            }
            case (3, 4): // IA4 = I3 + A1
            {
                var v = Nibble(row, x);
                var i = (byte)((v >> 1) * 255 / 7);
                return (i, i, i, (byte)((v & 1) * 255));
            }
            case (3, 8): // IA8 = I4 + A4
            {
                var v = row[x];
                var i = (byte)((v >> 4) * 17);
                return (i, i, i, (byte)((v & 0xF) * 17));
            }
            case (4, 4): // I4
            {
                var i = (byte)(Nibble(row, x) * 17);
                return (i, i, i, 255);
            }
            case (4, 8): // I8
            {
                var i = row[x];
                return (i, i, i, 255);
            }
            default:
                throw new InvalidDataException($"Unhandled N64 texel format ({format},{bitsPerTexel})");
        }
    }

    private static byte Nibble(ReadOnlySpan<byte> row, int x)
    {
        var b = row[x / 2];
        return (byte)((x & 1) == 0 ? b >> 4 : b & 0xF);
    }

    private static (byte, byte, byte, byte) Rgba5551(ushort v)
    {
        return (
            (byte)(((v >> 11) & 31) * 255 / 31),
            (byte)(((v >> 6) & 31) * 255 / 31),
            (byte)(((v >> 1) & 31) * 255 / 31),
            (byte)((v & 1) * 255));
    }

    /// <summary>
    ///     CI4 palette: 16 BIG-endian RGBA5551 words starting ONE BYTE BEFORE
    ///     the nominal pixel-data end — entry e = (data[palOff-1+2e] &lt;&lt; 8)
    ///     | data[palOff+2e], so entry 0's high byte overlaps the last pixel
    ///     byte and the region's final byte is pad. Solved 2026-08-05 against
    ///     PS1 CLUT ground truth (user-reported "solid white decodes solid
    ///     blue"): reading aligned LE words instead shifts every entry's high
    ///     byte to its neighbour — mid-palette entries land one brightness
    ///     step off (the "shifted palette" symptom) and the top entry's white
    ///     collapses to a blue (low byte 0xFF alone). With the odd alignment
    ///     the skven porta-potty palette reproduces its PS1 CLUT color-for-
    ///     color (brightness re-sorted) and the l1a1 scoring digits become an
    ///     exact gray ramp. Returned normalized to LE pairs for DecodeTexel.
    /// </summary>
    private static byte[] ReadPalette(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if (offset + 32 > data.Length || offset < 1)
            throw new InvalidDataException("N64 CI4 record is missing its palette");
        _ = littleEndian;
        var palette = new byte[32];
        for (var e = 0; e < 16; e++)
        {
            palette[2 * e] = data[offset + 2 * e];         // low byte (aligned)
            palette[2 * e + 1] = data[offset - 1 + 2 * e]; // high byte (one byte early)
        }

        return palette;
    }

    private static string FormatName(int format, int bitsPerTexel)
    {
        return (format, bitsPerTexel) switch
        {
            (0, 16) => "RGBA16",
            (2, 4) => "CI4",
            (3, 4) => "IA4",
            (3, 8) => "IA8",
            (4, 4) => "I4",
            (4, 8) => "I8",
            _ => $"({format},{bitsPerTexel})"
        };
    }

    // ---- image records ------------------------------------------------------

    public static bool IsImageRecord(ReadOnlySpan<byte> data)
    {
        return TryReadImageHeader(data, out _, out _, out _, out _, out _, out _);
    }

    public static N64Texture DecodeImageRecord(byte[] data)
    {
        if (!TryReadImageHeader(data, out var width, out var height, out var colors,
                out var stride, out var paletteOffset, out var pixelOffset))
        {
            throw new InvalidDataException("Not an N64 image record");
        }

        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = data[pixelOffset + y * stride + x];
                var o = paletteOffset + Math.Min(index, colors - 1) * 2;
                var v = (ushort)((data[o] << 8) | data[o + 1]); // image palettes are BE
                var (r, g, b, a) = Rgba5551(v);
                var p = (y * width + x) * 4;
                rgba[p] = r;
                rgba[p + 1] = g;
                rgba[p + 2] = b;
                rgba[p + 3] = a;
            }
        }

        return new N64Texture(null, width, height, "CI8", rgba);
    }

    private static bool TryReadImageHeader(
        ReadOnlySpan<byte> data,
        out int width,
        out int height,
        out int colors,
        out int stride,
        out int paletteOffset,
        out int pixelOffset)
    {
        width = height = colors = stride = paletteOffset = pixelOffset = 0;
        if (data.Length < 4 + 4 * 4 + 28)
            return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != 3)
            return false;

        Span<int> offsets = stackalloc int[4];
        for (var k = 0; k < 4; k++)
        {
            offsets[k] = BinaryPrimitives.ReadInt32BigEndian(data[(4 + 4 * k)..]);
            if (offsets[k] < 0 || offsets[k] > data.Length || (k > 0 && offsets[k] < offsets[k - 1]))
                return false;
        }

        if (offsets[1] - offsets[0] != 28)
            return false;
        var header = data[offsets[0]..];
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != ImageMagic)
            return false;

        width = BinaryPrimitives.ReadInt32BigEndian(header[8..]);
        height = BinaryPrimitives.ReadInt32BigEndian(header[12..]);
        colors = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        stride = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        paletteOffset = offsets[1];
        pixelOffset = offsets[2];
        return width is >= 1 and <= 1024
               && height is >= 1 and <= 1024
               && colors is >= 1 and <= 256
               && stride >= width
               && offsets[2] - offsets[1] >= colors * 2
               && offsets[3] - offsets[2] >= stride * height;
    }
}
