namespace NeversoftMultitool.Core.Formats.Texture.Ps2;

/// <summary>
///     Parses a version-2 .img.ps2 single-texture file (THPS4/THUG/THUG2 sprites, fonts,
///     icons and loadscreens). Format from THUG source Gfx/NGPS/NX/sprite.cpp:
///     version(u32), checksum(u32), TW(u32), TH(u32), PSM(u32), CPSM(u32), MXL(u32),
///     orig_width(u16), orig_height(u16), [pad to 16], CLUT, pixel data.
///     The CLUT is stored linear (CSM0): the engine applies the CSM1 rearrange itself at
///     load time (sprite.cpp setup_reg_and_dma), so extraction uses it as-is.
///     Pixel data ships in one of two layouts, distinguished by region size:
///     tight orig_width x orig_height rows (fonts, loadscreens), or the full
///     (1&lt;&lt;TW) x (1&lt;&lt;TH) VRAM upload buffer that sprite.cpp DMAs in one piece
///     (NumTexBytes = Width*Height*BitsPerTexel>>3 with POT Width/Height). Non-POT
///     sprites in the latter layout are de-strided to their original dimensions here.
///     THUG2 additionally sets a GS-SWIZZLE flag in the MXL word's high bits
///     (see <see cref="SwizzleFlags" />): the build tool pre-applied the same
///     Conv8to32/Conv4to32 rearrange the TEX v3-5 path undoes for MXL bit 30, so
///     those payloads unswizzle at the stored buffer's dimensions before the
///     de-stride. THUG-era files leave the word zero. Measured 2026-08-26.
/// </summary>
internal static class Ps2ImgV2File
{
    /// <summary>
    ///     Bits 20-21 of the MXL word mark GS-swizzled pixel data (THUG2). The
    ///     corpus carries exactly three values in that word — 0 (9,929 files),
    ///     0x00100000 (728) and 0x00200000 (1,901) — so the mip count in the low
    ///     bits is always zero and both flag values take the identical decode.
    ///     Proven against Remix PSP twins (see the class remarks): with the
    ///     rearrange undone, 1,185 of 1,185 comparable flagged files decode
    ///     pixel-identical to their PSP siblings, while leaving the data linear
    ///     matches only 62 and reading it as the PSMCT16 upload variant only 370.
    /// </summary>
    private const uint SwizzleFlags = 0x00300000;

    /// <summary>Parses a version-2 IMG file and returns its single texture.</summary>
    internal static Ps2TexResult Parse(byte[] data)
    {
        if (data.Length < 32) return Ps2TexResult.Fail("IMG file too small");

        var checksum = BitConverter.ToUInt32(data, 4);
        var tw = BitConverter.ToUInt32(data, 8);
        var th = BitConverter.ToUInt32(data, 12);
        var psm = BitConverter.ToUInt32(data, 16);
        var cpsm = BitConverter.ToUInt32(data, 20);
        // THUG Gfx/NGPS/NX/sprite.cpp explicitly Dbg_Assert(mxl == 0) for this
        // version-2 single-image grammar, so a real mip count is still refused;
        // only the THUG2 swizzle bits are accepted on top of it.
        var mxlWord = BitConverter.ToUInt32(data, 24);
        var gsSwizzled = (mxlWord & SwizzleFlags) != 0;
        if ((mxlWord & ~SwizzleFlags) != 0)
            return Ps2TexResult.Fail($"IMG MXL must be zero (found 0x{mxlWord:X8})");

        // Actual image dimensions (may differ from 1<<TW / 1<<TH for non-POT sprites)
        var origWidth = (int)BitConverter.ToUInt16(data, 28);
        var origHeight = (int)BitConverter.ToUInt16(data, 30);

        if (tw > 11 || th > 11) return Ps2TexResult.Fail($"Invalid dimensions TW={tw} TH={th}");
        if (!Ps2TexPixelDecoder.IsValidPsm(psm)) return Ps2TexResult.Fail($"Invalid PSM 0x{psm:X2}");

        var vramWidth = 1 << (int)tw;
        var vramHeight = 1 << (int)th;
        var width = origWidth > 0 ? origWidth : vramWidth;
        var height = origHeight > 0 ? origHeight : vramHeight;
        var pixelCount = (long)width * height;
        if (pixelCount > Array.MaxLength / 4L)
            return Ps2TexResult.Fail(
                $"IMG dimensions {width}x{height} exceed the supported RGBA pixel buffer");

        var offset = 32; // fixed header size, already 16-byte aligned

        // Read CLUT (linear CSM0 order -- no unswizzle, see class remarks)
        byte[]? clut = null;
        var paletteSize = Ps2TexPixelDecoder.GetPaletteSize(psm);
        if (paletteSize > 0)
        {
            var clutBytes = paletteSize * Ps2TexPixelDecoder.GetBitsPerPixel(cpsm) / 8;
            if (clutBytes <= 0 || offset + clutBytes > data.Length)
                return Ps2TexResult.Fail("CLUT data truncated");

            clut = new byte[clutBytes];
            Array.Copy(data, offset, clut, 0, clutBytes);
            offset = (offset + clutBytes + 15) & ~15;
        }

        var bpp = Ps2TexPixelDecoder.GetBitsPerPixel(psm);
        var origBytes = (int)((pixelCount * bpp + 7) / 8);
        var vramBytes = vramWidth * vramHeight * bpp / 8;
        var availableBytes = data.Length - offset;

        ReadOnlySpan<byte> texData;
        var storedAsVramBuffer = availableBytes == vramBytes && width <= vramWidth && height <= vramHeight;
        if (storedAsVramBuffer && vramBytes != origBytes)
        {
            // Full VRAM upload buffer: rows padded to the POT stride. The swizzle
            // (when present) covers the whole stored buffer, so it is undone at the
            // POT dimensions before de-striding to the original size.
            var buffer = Unswizzle(data.AsSpan(offset, vramBytes), vramWidth, vramHeight, psm, gsSwizzled);
            texData = DestridePixels(buffer, vramWidth, vramHeight, width, height, bpp);
        }
        else if (storedAsVramBuffer)
        {
            // POT sprite: the stored buffer already is the image.
            texData = Unswizzle(data.AsSpan(offset, vramBytes), vramWidth, vramHeight, psm, gsSwizzled);
        }
        else
        {
            // Tight orig-stride storage (fonts, loadscreens)
            if (availableBytes < origBytes)
                return Ps2TexResult.Fail("Failed to decode pixel data");
            texData = Unswizzle(data.AsSpan(offset, origBytes), width, height, psm, gsSwizzled);
        }

        var pixels = Ps2TexPixelDecoder.DecodePixels(texData, width, height, psm, cpsm, clut);
        if (pixels == null) return Ps2TexResult.Fail("Failed to decode pixel data");

        return new Ps2TexResult([new Ps2Texture(checksum, width, height, psm, cpsm, pixels)]);
    }

    /// <summary>
    ///     Undoes the THUG2 build tool's GS rearrange over a stored buffer, using
    ///     the same page mappings the TEX v3-5 path applies for MXL bit 30. Only
    ///     paletted formats are ever swizzled; anything else (and any unflagged
    ///     file) passes through untouched.
    /// </summary>
    private static ReadOnlySpan<byte> Unswizzle(
        ReadOnlySpan<byte> stored, int bufferWidth, int bufferHeight, uint psm, bool gsSwizzled)
    {
        if (!gsSwizzled)
            return stored;

        if (psm == Ps2TexPixelDecoder.PSMT8)
            return Ps2TexSwizzle.UnswizzlePsmt8(stored, bufferWidth, bufferHeight);
        if (psm == Ps2TexPixelDecoder.PSMT4)
            return Ps2TexSwizzle.UnswizzlePsmt4(stored, bufferWidth, bufferHeight);

        return stored;
    }

    /// <summary>
    ///     Extracts origW x origH raw pixel data from a POT VRAM upload buffer with
    ///     stride = srcW pixels. Rows are stored bottom-up (DecodePixels un-flips), so the
    ///     image content occupies the LAST origH rows of the srcH-tall buffer -- same
    ///     convention as the THAW v4 path in <see cref="Ps2TexFile" />.
    /// </summary>
    private static byte[] DestridePixels(ReadOnlySpan<byte> src, int srcW, int srcH,
        int origW, int origH, int bpp)
    {
        var startRow = srcH - origH;
        if (bpp == 4)
            return DestrideNibbles(src, srcW, startRow, origW, origH);

        var srcStride = srcW * bpp / 8;
        var dstStride = origW * bpp / 8;
        var dst = new byte[dstStride * origH];
        for (var y = 0; y < origH; y++)
            src.Slice((startRow + y) * srcStride, dstStride).CopyTo(dst.AsSpan(y * dstStride, dstStride));
        return dst;
    }

    /// <summary>
    ///     PSMT4 variant of <see cref="DestridePixels" /> operating on 4-bit pixels, needed
    ///     because odd orig widths (e.g. 121x33 panel sprites) produce rows that are not
    ///     byte-aligned. Produces a packed nibble stream (low nibble first) of
    ///     origW*origH pixels, as Ps2TexPixelDecoder.DecodePsmt4 expects.
    /// </summary>
    private static byte[] DestrideNibbles(ReadOnlySpan<byte> src, int srcW, int startRow,
        int origW, int origH)
    {
        var dst = new byte[(origW * origH + 1) / 2];
        for (var y = 0; y < origH; y++)
        {
            for (var x = 0; x < origW; x++)
            {
                var srcIndex = (startRow + y) * srcW + x;
                var nibble = (srcIndex & 1) == 0
                    ? src[srcIndex >> 1] & 0x0F
                    : (src[srcIndex >> 1] >> 4) & 0x0F;
                var dstIndex = y * origW + x;
                dst[dstIndex >> 1] |= (byte)((dstIndex & 1) == 0 ? nibble : nibble << 4);
            }
        }

        return dst;
    }
}
