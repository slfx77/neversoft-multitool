using System.Text;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
/// Parses RenderWare 3.x Texture Dictionary (TXD) files to extract textures as RGBA pixel data.
/// Used for THPS3 PS2 .tex files which use RenderWare format instead of Neversoft's custom binary.
/// RW version 3.1.0 (stamp 0x0310). Chunk-based container with PS2-native rasters.
/// Format reference: librw (aap/librw), GTAModding wiki, hex analysis of THPS3 files.
/// </summary>
public static class RwTxdFile
{
    // RenderWare chunk types
    private const uint RW_STRUCT = 0x0001;
    private const uint RW_STRING = 0x0002;
    private const uint RW_TEX_DICT = 0x0016;
    private const uint RW_TEX_NATIVE = 0x0015;

    // RW rasterFormat flags
    private const int FMT_PAL4 = 0x4000;
    private const int FMT_PAL8 = 0x2000;
    private const int FMT_MASK = 0x0F00;
    private const int FMT_C1555 = 0x0100; // ABGR1555 palette entries (16-bit)

    /// <summary>
    /// Parses an RW TXD file and returns all extracted textures.
    /// </summary>
    public static Ps2TexResult Parse(string filePath)
    {
        try
        {
            return Parse(File.ReadAllBytes(filePath));
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Parses an RW TXD from a byte array.
    /// </summary>
    public static Ps2TexResult Parse(byte[] data)
    {
        try
        {
            if (data.Length < 12)
                return Ps2TexResult.Fail("File too small");

            var offset = 0;
            var (type, size, _) = ReadChunkHeader(data, ref offset);
            if (type != RW_TEX_DICT)
                return Ps2TexResult.Fail($"Not an RW TexDict (got 0x{type:X4})");

            var dictEnd = offset + (int)size;

            // First child: Struct with textureCount(u16) + deviceId(u16)
            var (sType, sSize, _) = ReadChunkHeader(data, ref offset);
            if (sType != RW_STRUCT)
                return Ps2TexResult.Fail("Expected Struct chunk in TexDict");

            var textureCount = BitConverter.ToUInt16(data, offset);
            offset += (int)sSize;

            var textures = new List<Ps2Texture>();

            for (var i = 0; i < textureCount && offset < dictEnd; i++)
            {
                var (tType, tSize, _2) = ReadChunkHeader(data, ref offset);
                var texEnd = offset + (int)tSize;

                if (tType == RW_TEX_NATIVE)
                {
                    var tex = ParseTextureNative(data, ref offset, texEnd);
                    if (tex != null)
                        textures.Add(tex);
                }

                offset = texEnd; // ensure we advance past the entire chunk
            }

            return new Ps2TexResult(textures);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Parses one TextureNative chunk: platform info, name, mask, raster data.
    /// </summary>
    private static Ps2Texture? ParseTextureNative(byte[] data, ref int offset, int endOffset)
    {
        // Child 1: Struct — platformId("PS2\0") + filterAddressing(u32)
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var s1Size))
            return null;
        offset += (int)s1Size;

        // Child 2: String — texture name
        if (!TryReadChunk(data, ref offset, endOffset, RW_STRING, out var nameSize))
            return null;
        var name = ReadNullTerminatedString(data, offset, (int)nameSize);
        offset += (int)nameSize;

        // Child 3: String — mask name (usually empty, skip)
        if (!TryReadChunk(data, ref offset, endOffset, RW_STRING, out var maskSize))
            return null;
        offset += (int)maskSize;

        // Child 4: Struct — raster container
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var rasterContainerSize))
            return null;
        var rasterEnd = offset + (int)rasterContainerSize;

        // Raster sub-child 1: Struct — StreamRasterExt header (64 bytes)
        if (!TryReadStruct(data, ref offset, endOffset, out _, out var headerSize))
            return null;
        if (headerSize < 64 || offset + 64 > data.Length)
            return null;

        var hdr = offset;
        var width = BitConverter.ToInt32(data, hdr);
        var height = BitConverter.ToInt32(data, hdr + 4);
        var depth = BitConverter.ToInt32(data, hdr + 8);
        var rasterFormat = BitConverter.ToUInt16(data, hdr + 12);
        // version at hdr+14, tex0 at hdr+16, paletteOffset at hdr+24, tex1low at hdr+28
        // miptbp1 at hdr+32, miptbp2 at hdr+40
        var pixelSize = BitConverter.ToInt32(data, hdr + 48);
        var paletteSize = BitConverter.ToInt32(data, hdr + 52);
        offset += (int)headerSize;

        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            return null;

        // Raster sub-child 2: Struct — pixel + palette data (combined in one chunk for version 0)
        if (!TryReadStruct(data, ref offset, rasterEnd, out _, out var dataChunkSize))
            return null;
        var pixelDataOffset = offset;
        var actualPixelSize = Math.Min(pixelSize, (int)dataChunkSize);

        // Palette follows pixel data within the same Struct chunk
        byte[]? paletteData = null;
        if (paletteSize > 0)
        {
            var paletteOffset = offset + pixelSize;
            if (paletteOffset + paletteSize <= offset + (int)dataChunkSize &&
                paletteOffset + paletteSize <= data.Length)
            {
                paletteData = new byte[paletteSize];
                Array.Copy(data, paletteOffset, paletteData, 0, paletteSize);
            }
        }

        offset += (int)dataChunkSize;

        // Decode to RGBA
        var pixelData = data.AsSpan(pixelDataOffset, actualPixelSize);
        var pixels = DecodeRaster(pixelData, width, height, depth, rasterFormat, paletteData);
        if (pixels == null)
            return null;

        // Strip file extension from texture name for cleaner output filenames
        var displayName = name;
        var extIdx = name.LastIndexOf('.');
        if (extIdx > 0)
            displayName = name[..extIdx];

        // Use CRC-32 hash of the name for the Checksum field
        var checksum = Core.QbKey.Hash(name);

        // Determine PSM from depth/format for metadata
        var psm = DepthToPsm(depth, rasterFormat);

        return new Ps2Texture(checksum, width, height, psm, 0x00, pixels, displayName);
    }

    private static byte[]? DecodeRaster(ReadOnlySpan<byte> pixelData, int width, int height,
        int depth, int rasterFormat, byte[]? paletteData)
    {
        var pixels = new byte[width * height * 4];
        var isPal4 = (rasterFormat & FMT_PAL4) != 0;
        var isPal8 = (rasterFormat & FMT_PAL8) != 0;

        // Expand 16-bit ABGR1555 palette to 32-bit RGBA if needed
        if (paletteData != null && (rasterFormat & FMT_MASK) == FMT_C1555)
            paletteData = ExpandClut16To32(paletteData);

        if (isPal4 && depth == 4 && paletteData != null)
        {
            DecodePsmt4(pixelData, pixels, width, height, paletteData);
        }
        else if (isPal8 && depth == 8 && paletteData != null)
        {
            // PSMT8 256-entry CLUTs are stored in CSM1 order — un-swizzle after
            // any 16-bit→32-bit expansion so we always operate on 4-byte entries
            SwizzleCsm1(paletteData);
            DecodePsmt8(pixelData, pixels, width, height, paletteData);
        }
        else if (depth == 32)
        {
            DecodePsmct32(pixelData, pixels, width, height);
        }
        else if (depth == 16)
        {
            DecodePsmct16(pixelData, pixels, width, height);
        }
        else
        {
            return null;
        }

        FixAllZeroAlpha(pixels);
        return pixels;
    }

    // ── Pixel decode methods (self-contained, matching PS2 GS formats) ──

    private static void DecodePsmct32(ReadOnlySpan<byte> src, byte[] dst, int width, int height)
    {
        var count = Math.Min(width * height, src.Length / 4);
        for (var i = 0; i < count; i++)
        {
            var si = i * 4;
            var di = i * 4;
            dst[di] = src[si];         // R
            dst[di + 1] = src[si + 1]; // G
            dst[di + 2] = src[si + 2]; // B
            dst[di + 3] = ScaleAlpha(src[si + 3]);
        }
    }

    private static void DecodePsmct16(ReadOnlySpan<byte> src, byte[] dst, int width, int height)
    {
        var count = Math.Min(width * height, src.Length / 2);
        for (var i = 0; i < count; i++)
        {
            var si = i * 2;
            var pixel = (ushort)(src[si] | (src[si + 1] << 8));
            var di = i * 4;
            dst[di] = (byte)((pixel & 0x1F) << 3 | (pixel & 0x1F) >> 2);
            dst[di + 1] = (byte)(((pixel >> 5) & 0x1F) << 3 | ((pixel >> 5) & 0x1F) >> 2);
            dst[di + 2] = (byte)(((pixel >> 10) & 0x1F) << 3 | ((pixel >> 10) & 0x1F) >> 2);
            dst[di + 3] = 255;
        }
    }

    private static void DecodePsmt8(ReadOnlySpan<byte> src, byte[] dst, int width, int height,
        byte[] clut)
    {
        var count = Math.Min(width * height, src.Length);
        for (var i = 0; i < count; i++)
        {
            var ci = src[i] * 4;
            var di = i * 4;
            if (ci + 4 <= clut.Length)
            {
                dst[di] = clut[ci];
                dst[di + 1] = clut[ci + 1];
                dst[di + 2] = clut[ci + 2];
                dst[di + 3] = ScaleAlpha(clut[ci + 3]);
            }
        }
    }

    private static void DecodePsmt4(ReadOnlySpan<byte> src, byte[] dst, int width, int height,
        byte[] clut)
    {
        var totalPixels = width * height;
        for (var i = 0; i < totalPixels; i++)
        {
            var byteIndex = i >> 1;
            if (byteIndex >= src.Length) break;

            var colorIndex = (i & 1) == 0
                ? src[byteIndex] & 0x0F
                : (src[byteIndex] >> 4) & 0x0F;

            var ci = colorIndex * 4;
            var di = i * 4;
            if (ci + 4 <= clut.Length)
            {
                dst[di] = clut[ci];
                dst[di + 1] = clut[ci + 1];
                dst[di + 2] = clut[ci + 2];
                dst[di + 3] = ScaleAlpha(clut[ci + 3]);
            }
        }
    }

    /// <summary>
    /// CSM1 CLUT swizzle for PSMT8: swap entries [j+8..j+15] with [j+16..j+23] in each group of 32.
    /// </summary>
    private static void SwizzleCsm1(byte[] clut)
    {
        const int entrySize = 4; // RGBA32
        for (var group = 0; group < 256; group += 32)
        {
            for (var i = 0; i < 8; i++)
            {
                var a = (group + 8 + i) * entrySize;
                var b = (group + 16 + i) * entrySize;
                if (a + entrySize > clut.Length || b + entrySize > clut.Length) break;

                for (var c = 0; c < entrySize; c++)
                    (clut[a + c], clut[b + c]) = (clut[b + c], clut[a + c]);
            }
        }
    }

    /// <summary>
    /// Expands a 16-bit ABGR1555 CLUT to 32-bit RGBA (4 bytes per entry).
    /// PS2 GS PSMCT16 layout: bit 15 = A, bits 10-14 = B, bits 5-9 = G, bits 0-4 = R.
    /// </summary>
    private static byte[] ExpandClut16To32(byte[] clut16)
    {
        var entryCount = clut16.Length / 2;
        var clut32 = new byte[entryCount * 4];
        for (var i = 0; i < entryCount; i++)
        {
            var pixel = (ushort)(clut16[i * 2] | (clut16[i * 2 + 1] << 8));
            var r = pixel & 0x1F;
            var g = (pixel >> 5) & 0x1F;
            var b = (pixel >> 10) & 0x1F;
            var a = (pixel >> 15) & 1;
            clut32[i * 4] = (byte)(r << 3 | r >> 2);
            clut32[i * 4 + 1] = (byte)(g << 3 | g >> 2);
            clut32[i * 4 + 2] = (byte)(b << 3 | b >> 2);
            clut32[i * 4 + 3] = (byte)(a * 255);
        }
        return clut32;
    }

    private static byte ScaleAlpha(byte gsAlpha) =>
        (byte)Math.Min(gsAlpha * 255 / 128, 255);

    private static void FixAllZeroAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return;
        }

        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
    }

    // ── RW chunk reading helpers ──

    private static (uint type, uint size, uint version) ReadChunkHeader(byte[] data, ref int offset)
    {
        var type = BitConverter.ToUInt32(data, offset);
        var size = BitConverter.ToUInt32(data, offset + 4);
        var version = BitConverter.ToUInt32(data, offset + 8);
        offset += 12;
        return (type, size, version);
    }

    private static bool TryReadStruct(byte[] data, ref int offset, int endOffset,
        out uint type, out uint size)
    {
        type = 0;
        size = 0;
        if (offset + 12 > endOffset || offset + 12 > data.Length) return false;

        type = BitConverter.ToUInt32(data, offset);
        size = BitConverter.ToUInt32(data, offset + 4);
        offset += 12;
        return type == RW_STRUCT;
    }

    private static bool TryReadChunk(byte[] data, ref int offset, int endOffset,
        uint expectedType, out uint size)
    {
        size = 0;
        if (offset + 12 > endOffset || offset + 12 > data.Length) return false;

        var type = BitConverter.ToUInt32(data, offset);
        size = BitConverter.ToUInt32(data, offset + 4);
        offset += 12;
        return type == expectedType;
    }

    private static string ReadNullTerminatedString(byte[] data, int offset, int maxLength)
    {
        var end = offset + maxLength;
        if (end > data.Length) end = data.Length;

        var len = 0;
        while (offset + len < end && data[offset + len] != 0)
            len++;

        return Encoding.ASCII.GetString(data, offset, len);
    }

    private static uint DepthToPsm(int depth, int rasterFormat) => depth switch
    {
        4 when (rasterFormat & FMT_PAL4) != 0 => 0x14,  // PSMT4
        8 when (rasterFormat & FMT_PAL8) != 0 => 0x13,  // PSMT8
        32 => 0x00, // PSMCT32
        16 => 0x02, // PSMCT16
        _ => 0xFF
    };

    public static string DescribeDepth(int depth) => depth switch
    {
        4 => "4-bit indexed",
        8 => "8-bit indexed",
        16 => "16bpp RGBA5551",
        32 => "32bpp RGBA",
        _ => $"Unknown ({depth}bpp)"
    };
}
