using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

/// <summary>
///     Authoritative THAW PS2 zone .tex decoder based on the FUN_001e9ac0 owner blob format.
///
///     This is the format the runtime actually uses to load zone textures. It mirrors the
///     decompiled FUN_001e9ac0 logic from THAW PS2:
///
///     1. The file begins (after some prefix data) with a 0x10-byte owner blob header:
///         <code>
///         +0x00 u16  global_u16
///         +0x02 u16  primary_count
///         +0x04 i32  secondary_count   (number of texture records)
///         +0x08 i32  base_a_offset     (used to relocate per-record +0x38)
///         +0x0c i32  base_b_offset     (used to relocate per-record +0x28 / +0x30)
///         </code>
///         followed immediately by primary_count * 0x50 bytes of primaries, then
///         secondary_count * 0x40 bytes of secondaries (texture records).
///
///     2. Each secondary (0x40 bytes) at the standard layout described in zone_tex_format.md.
///         FUN_001e9ac0 relocates fields per record:
///         <code>
///         relocated cumul_off    = header_base + record.cumul_off    + base_b
///         relocated data_offset  = header_base + record.data_offset  + base_b
///         relocated upload_off   = header_base + record.upload_off   + base_a
///         </code>
///
///     3. After relocation:
///         - <c>relocated_data_offset</c> points at the prepared CLUT data (32 bytes for
///           PSMCT16, 64 bytes for PSMCT32).
///         - <c>relocated_cumul_off</c> points at the prepared pixel data. Storage
///           layout depends on (psm, tw, th):
///           PSMT4 follows the standard build-tool path
///           (<see cref="Ps2TexSwizzle.UnswizzlePsmt4"/>: Conv4to32 -> Conv4to16 -> linear).
///           PSMT8 uses Conv8to32 for everything except sub-page-width
///           multi-page-tall records (tw &lt; 128 AND th &gt; 64), which are
///           linear bottom-up; FUN_0019cd48 (Phase 333) confirms that path.
///
///     4. Decode:
///         - PSMT4: walk the unswizzled nibble buffer bottom-up, look up in the CLUT.
///         - PSMT8: walk the unswizzled / linear byte buffer bottom-up, apply the
///           CSM1 index remap (DAT_005ad180), then look up in the CLUT.
///
///     This replaces the older heuristic <see cref="ThawZoneTexFile.TryGetHeaderDataLayout"/>
///     path which guessed the data base offset and the per-record layout.
/// </summary>
internal static class ThawZoneTexOwnerBlobDecoder
{
    /// <summary>
    ///     Try to locate the FUN_001e9ac0 owner blob header in the zone .tex file.
    ///     Returns false if no header could be found.
    /// </summary>
    /// <remarks>
    ///     The header position is found by scanning for any (header_offset, primary_count,
    ///     secondary_count) triple such that
    ///     <c>header_offset + 0x10 + primary_count * 0x50 + secondary_count * 0x40 == dma_chain_start</c>,
    ///     where the DMA chain start is the first <c>0x10000006</c> CNT tag in the file.
    /// </remarks>
    internal static bool TryFindOwnerBlobHeader(
        ReadOnlySpan<byte> data,
        out int headerOffset,
        out int primaryCount,
        out int secondaryCount,
        out int baseAOffset,
        out int baseBOffset,
        out int dmaChainStart)
    {
        headerOffset = 0;
        primaryCount = 0;
        secondaryCount = 0;
        baseAOffset = 0;
        baseBOffset = 0;
        dmaChainStart = 0;

        // Find DMA chain start (first 0x10000006 CNT tag followed by GIF A+D)
        var dmaStart = -1;
        for (var off = 0; off + 32 <= data.Length; off += 16)
        {
            var tag = BitConverter.ToUInt32(data[off..]);
            if (tag != 0x10000006u) continue;
            var gifLo = BitConverter.ToUInt64(data[(off + 16)..]);
            var gifHi = BitConverter.ToUInt64(data[(off + 24)..]);
            if (gifHi != 0x0E) continue;
            var nloop = (int)(gifLo & 0x7FFF);
            if (nloop is < 1 or > 32) continue;
            dmaStart = off;
            break;
        }

        if (dmaStart < 0) return false;

        // Scan for a header that ends exactly at dmaStart. Start at 0x10 (after the
        // minimal file header) to support both small .stex companion files (header
        // near the beginning, around 0xC0) and large zone .tex files (header deeper
        // in the file, around 0xFC0). The validation below (prim/sec ranges plus
        // layout-end-matches-dma-start) keeps false positives out.
        for (var off = 0x10; off + 0x10 < dmaStart; off += 4)
        {
            var prim = BitConverter.ToUInt16(data[(off + 2)..]);
            var sec = BitConverter.ToInt32(data[(off + 4)..]);
            if (prim is < 0 or > 200) continue;
            if (sec is < 1 or > 5000) continue;
            var expectedEnd = off + 0x10 + prim * 0x50 + sec * 0x40;
            if (expectedEnd != dmaStart) continue;
            var ba = BitConverter.ToInt32(data[(off + 8)..]);
            var bb = BitConverter.ToInt32(data[(off + 0xc)..]);
            if (ba < 0 || ba >= data.Length) continue;
            if (bb < 0 || bb >= data.Length) continue;
            headerOffset = off;
            primaryCount = prim;
            secondaryCount = sec;
            baseAOffset = ba;
            baseBOffset = bb;
            dmaChainStart = dmaStart;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Decode all PSMT4 records from a zone .tex file using the owner-blob path.
    ///     Returns the decoded textures keyed by checksum order.
    /// </summary>
    internal static List<Ps2Texture> DecodeAllRecords(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry> entries)
    {
        var result = new List<Ps2Texture>();

        if (!TryFindOwnerBlobHeader(fileData, out var headerBase, out _, out _,
                out var baseA, out var baseB, out var dmaStart))
            return result;

        foreach (var entry in entries)
        {
            var texture = DecodeRecord(fileData, entry, headerBase, baseA, baseB, dmaStart);
            if (texture != null)
                result.Add(texture);
        }

        return result;
    }

    /// <summary>
    ///     Decode a single record using the FUN_001e9ac0 relocation formula.
    /// </summary>
    /// <remarks>
    ///     If every output pixel is alpha-zero but the buffer has visible RGB
    ///     content, force alpha to 255. This matches the
    ///     <see cref="Ps2TexPixelDecoder.DecodePixels"/> post-pass and recovers
    ///     PSMT8 records whose CLUT/index combination decodes every base-level
    ///     pixel to alpha-zero (the PS2 "use material/register alpha, not
    ///     per-texel alpha" convention). Records whose lower mip carries real
    ///     alpha are handled before this fixup.
    /// </remarks>
    internal static Ps2Texture? DecodeRecord(
        ReadOnlySpan<byte> fileData,
        ThawZoneTexFile.ZoneTexHeaderEntry entry,
        int headerBase,
        int baseA,
        int baseB,
        int dmaChainStart)
    {
        var tex0 = entry.Tex0;
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var cpsm = (uint)((tex0 >> 51) & 0xF);
        var tw = 1 << (int)((tex0 >> 26) & 0xF);
        var th = 1 << (int)((tex0 >> 30) & 0xF);

        if (psm != Ps2TexPixelDecoder.PSMT4 && psm != Ps2TexPixelDecoder.PSMT8
            && psm != Ps2TexPixelDecoder.PSMCT32 && psm != Ps2TexPixelDecoder.PSMCT16)
            return null;
        if (entry.DataSize == 0)
            return null;

        // Direct-color formats (no palette)
        if (psm == Ps2TexPixelDecoder.PSMCT32)
            return DecodePsmct32Record(fileData, entry, (long)headerBase + entry.CumulativeOffset + baseB, tw, th);
        if (psm == Ps2TexPixelDecoder.PSMCT16)
            return DecodePsmct16Record(fileData, entry, (long)headerBase + entry.CumulativeOffset + baseB, tw, th);

        if (entry.PaletteBytes == 0)
            return null;

        // Relocate per FUN_001e9ac0
        var pixelAbs = (long)headerBase + entry.CumulativeOffset + baseB;
        var clutAbs = (long)headerBase + entry.DataOffset + baseB;
        var pixelSize = psm == Ps2TexPixelDecoder.PSMT4 ? (long)tw * th / 2 : (long)tw * th;

        // Clamp pixel read to available file data (last record may extend past file end)
        if (pixelAbs < 0 || pixelAbs >= fileData.Length)
            return null;
        if (pixelAbs + pixelSize > fileData.Length)
            pixelSize = fileData.Length - pixelAbs;
        if (clutAbs < 0 || clutAbs + entry.PaletteBytes > fileData.Length)
            return null;

        var pixelBytes = fileData.Slice((int)pixelAbs, (int)pixelSize);
        var clutBytes = fileData.Slice((int)clutAbs, (int)entry.PaletteBytes);

        // Decode CLUT to RGBA32 palette
        // PSMT4: 16 entries, pal_bytes==32 (PSMCT16) or ==64 (PSMCT32)
        // PSMT8: 256 entries, pal_bytes==512 (PSMCT16) or ==1024 (PSMCT32)
        byte[] palette;
        var paletteEntries = psm == Ps2TexPixelDecoder.PSMT4 ? 16 : 256;
        var psmct16Bytes = paletteEntries * 2;
        var psmct32Bytes = paletteEntries * 4;
        if (entry.PaletteBytes == psmct16Bytes)
        {
            palette = ExpandPsmct16Palette(clutBytes, paletteEntries);
        }
        else if (entry.PaletteBytes == psmct32Bytes)
        {
            palette = ExpandPsmct32Palette(clutBytes, paletteEntries);
        }
        else
        {
            return null;
        }

        // Decode strategy:
        //  - PSMT4: full UnswizzlePsmt4 (Conv4to32 → Conv4to16 → linear fallback)
        //    matches the runtime build-tool layout for every dimension we see
        //    in zone .tex files. Phase 336 validated this path.
        //  - PSMT8: Conv8to32 except for sub-page-width AND multi-page-tall
        //    records (width < 128 AND height > 64). PSMT8 page geometry is
        //    128x64; in that quadrant the Conv8to32 algorithm zero-pads the
        //    right half of each page, producing visible checkerboard / banding
        //    (Phase 337). FUN_0019cd48 (Phase 333) reads those records linearly
        //    instead. 64x64 / 256x64 / 128x128 etc. continue through Conv8to32
        //    because they either fit a single page or fill the page width.
        // CSM1 index remap is applied for PSMT8 during palette lookup regardless
        // of which path produced the index buffer.
        const int psmt8PageWidth = 128;
        const int psmt8PageHeight = 64;
        var psmt8NeedsLinear = psm == Ps2TexPixelDecoder.PSMT8
            && tw < psmt8PageWidth
            && th > psmt8PageHeight;
        var indexBytes = psm switch
        {
            Ps2TexPixelDecoder.PSMT4 => Ps2TexSwizzle.UnswizzlePsmt4(pixelBytes, tw, th),
            Ps2TexPixelDecoder.PSMT8 when !psmt8NeedsLinear =>
                Ps2TexSwizzle.UnswizzlePsmt8(pixelBytes, tw, th),
            _ => null,
        };

        ReadOnlySpan<byte> sourceBytes = indexBytes ?? pixelBytes.ToArray();
        var rgba = RenderPalettedLinear(sourceBytes, palette, tw, th, psm);

        if (psm == Ps2TexPixelDecoder.PSMT8
            && IsAllAlphaZeroWithVisibleRgb(rgba)
            && TryDecodeAlphaBearingPsmt8Mip(fileData, pixelAbs, entry, palette, tw, th, out var mipTexture))
        {
            return new Ps2Texture(entry.Checksum, mipTexture.Width, mipTexture.Height, psm, cpsm, mipTexture.Rgba);
        }

        // PS2 textures whose CLUT entries are all alpha-0 use the GS material/
        // register alpha rather than per-texel alpha at runtime. The decoded RGBA
        // buffer would be invisible if we kept that as alpha-0; force-opaque it
        // (mirrors Ps2TexPixelDecoder.FixAllZeroAlpha for the public TEX path).
        FixAllZeroAlphaIfRgbVisible(rgba);

        return new Ps2Texture(entry.Checksum, tw, th, psm, cpsm, rgba);
    }

    private static bool TryDecodeAlphaBearingPsmt8Mip(
        ReadOnlySpan<byte> fileData,
        long pixelAbs,
        ThawZoneTexFile.ZoneTexHeaderEntry entry,
        byte[] palette,
        int baseWidth,
        int baseHeight,
        out (int Width, int Height, byte[] Rgba) mipTexture)
    {
        mipTexture = default;

        if (entry.MipLevelCount == 0 || entry.DataSize == 0)
            return false;
        if (pixelAbs < 0 || pixelAbs >= fileData.Length)
            return false;

        var availableBytes = Math.Min((long)entry.DataSize, fileData.Length - pixelAbs);
        if (availableBytes <= 0)
            return false;

        var offset = entry.BasePixelBytes != 0
            ? (long)entry.BasePixelBytes
            : (long)baseWidth * baseHeight;
        if (offset <= 0 || offset >= availableBytes)
            return false;

        for (var level = 1; level <= entry.MipLevelCount; level++)
        {
            var mipWidth = Math.Max(1, baseWidth >> level);
            var mipHeight = Math.Max(1, baseHeight >> level);
            var mipBytes = (long)mipWidth * mipHeight;
            if (offset + mipBytes > availableBytes)
                break;

            var mipPixelBytes = fileData.Slice((int)(pixelAbs + offset), (int)mipBytes);
            var psmt8NeedsLinear = mipWidth < 128 && mipHeight > 64;
            var mipIndexBytes = psmt8NeedsLinear
                ? mipPixelBytes.ToArray()
                : Ps2TexSwizzle.UnswizzlePsmt8(mipPixelBytes, mipWidth, mipHeight);
            var mipRgba = RenderPalettedLinear(mipIndexBytes, palette, mipWidth, mipHeight,
                Ps2TexPixelDecoder.PSMT8);

            if (HasAnyNonZeroAlpha(mipRgba))
            {
                mipTexture = (mipWidth, mipHeight, mipRgba);
                return true;
            }

            offset += mipBytes;
        }

        return false;
    }

    private static bool IsAllAlphaZeroWithVisibleRgb(byte[] rgba)
    {
        var hasRgb = false;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] != 0)
                return false;
            if (!hasRgb && (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0))
                hasRgb = true;
        }

        return hasRgb;
    }

    private static bool HasAnyNonZeroAlpha(byte[] rgba)
    {
        for (var i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     If every alpha byte in <paramref name="rgba"/> is 0 but at least one
    ///     RGB byte is non-zero, force all alpha to 255. This matches the public
    ///     <see cref="Ps2TexPixelDecoder.DecodePixels"/> "all-alpha-zero means
    ///     material-controlled alpha" convention. Skipping the fixup when RGB is
    ///     also zero keeps unused/empty record regions invisible rather than
    ///     turning them into solid black.
    /// </summary>
    private static void FixAllZeroAlphaIfRgbVisible(byte[] rgba)
    {
        var hasRgb = false;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] != 0) return; // any non-zero alpha — no fixup needed
            if (!hasRgb && (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0))
                hasRgb = true;
        }
        if (!hasRgb) return;

        for (var i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
    }

    /// <summary>
    ///     Linear bottom-up walk of a paletted index buffer into RGBA32. Out-of-range
    ///     reads fall back to palette entry 0 — the previous swizzle path zero-padded
    ///     to the full mapping size, so records whose stored pixel run is shorter
    ///     than tw*th retain the same trailing pixel value.
    /// </summary>
    private static byte[] RenderPalettedLinear(
        ReadOnlySpan<byte> pixelBytes, byte[] palette, int tw, int th, uint psm)
    {
        var rgba = new byte[tw * th * 4];
        for (var y = 0; y < th; y++)
        {
            var srcY = th - 1 - y;
            for (var x = 0; x < tw; x++)
            {
                var idx = psm == Ps2TexPixelDecoder.PSMT4
                    ? ReadPsmt4Index(pixelBytes, srcY, x, tw)
                    : ReadPsmt8IndexWithCsm1(pixelBytes, srcY, x, tw);
                var paletteOff = idx * 4;
                var outOff = (y * tw + x) * 4;
                rgba[outOff] = palette[paletteOff];
                rgba[outOff + 1] = palette[paletteOff + 1];
                rgba[outOff + 2] = palette[paletteOff + 2];
                rgba[outOff + 3] = palette[paletteOff + 3];
            }
        }
        return rgba;
    }

    private static int ReadPsmt4Index(ReadOnlySpan<byte> pixelBytes, int srcY, int x, int tw)
    {
        var nibblePos = srcY * tw + x;
        var byteIdx = nibblePos / 2;
        if (byteIdx >= pixelBytes.Length) return 0;
        var shift = (nibblePos & 1) * 4;
        return (pixelBytes[byteIdx] >> shift) & 0xF;
    }

    /// <summary>
    ///     Reads a PSMT8 index and applies the CSM1 remap (swap entries [8..15]
    ///     with [16..23] within each group of 32). Matches runtime DAT_005ad180
    ///     and THUG source texture.cpp:503-522.
    /// </summary>
    private static int ReadPsmt8IndexWithCsm1(ReadOnlySpan<byte> pixelBytes, int srcY, int x, int tw)
    {
        var byteIdx = srcY * tw + x;
        var idx = byteIdx >= pixelBytes.Length ? 0 : pixelBytes[byteIdx];
        var block = idx & 0x18;
        if (block == 0x08)
            idx = (idx & ~0x18) | 0x10;
        else if (block == 0x10)
            idx = (idx & ~0x18) | 0x08;
        return idx;
    }

    private static Ps2Texture? DecodePsmct32Record(
        ReadOnlySpan<byte> fileData,
        ThawZoneTexFile.ZoneTexHeaderEntry entry,
        long pixelAbs,
        int tw,
        int th)
    {
        var pixelSize = (long)tw * th * 4;

        if (pixelAbs < 0 || pixelAbs >= fileData.Length)
            return null;
        if (pixelAbs + pixelSize > fileData.Length)
            pixelSize = fileData.Length - pixelAbs;

        var srcBytes = fileData.Slice((int)pixelAbs, (int)pixelSize);

        // PSMCT32: 4 bytes per pixel (R, G, B, A), bottom-up
        var rgba = new byte[tw * th * 4];
        var srcPixels = (int)(pixelSize / 4);
        for (var y = 0; y < th; y++)
        {
            var srcY = th - 1 - y;
            for (var x = 0; x < tw; x++)
            {
                var srcIdx = srcY * tw + x;
                if (srcIdx >= srcPixels) continue;
                var srcOff = srcIdx * 4;
                var outOff = (y * tw + x) * 4;
                rgba[outOff] = srcBytes[srcOff];
                rgba[outOff + 1] = srcBytes[srcOff + 1];
                rgba[outOff + 2] = srcBytes[srcOff + 2];
                rgba[outOff + 3] = (byte)Math.Min(srcBytes[srcOff + 3] * 2, 255);
            }
        }

        return new Ps2Texture(entry.Checksum, tw, th,
            Ps2TexPixelDecoder.PSMCT32, 0, rgba);
    }

    private static Ps2Texture? DecodePsmct16Record(
        ReadOnlySpan<byte> fileData,
        ThawZoneTexFile.ZoneTexHeaderEntry entry,
        long pixelAbs,
        int tw,
        int th)
    {
        var pixelSize = (long)tw * th * 2;

        if (pixelAbs < 0 || pixelAbs >= fileData.Length)
            return null;
        if (pixelAbs + pixelSize > fileData.Length)
            pixelSize = fileData.Length - pixelAbs;

        var srcBytes = fileData.Slice((int)pixelAbs, (int)pixelSize);

        // PSMCT16: 2 bytes per pixel (RGBA5551), bottom-up
        var rgba = new byte[tw * th * 4];
        var srcPixels = (int)(pixelSize / 2);
        for (var y = 0; y < th; y++)
        {
            var srcY = th - 1 - y;
            for (var x = 0; x < tw; x++)
            {
                var srcIdx = srcY * tw + x;
                if (srcIdx >= srcPixels) continue;
                var pixel = BitConverter.ToUInt16(srcBytes[(srcIdx * 2)..]);
                var r = pixel & 0x1F;
                var g = (pixel >> 5) & 0x1F;
                var b = (pixel >> 10) & 0x1F;
                var outOff = (y * tw + x) * 4;
                rgba[outOff] = (byte)(r << 3);
                rgba[outOff + 1] = (byte)(g << 3);
                rgba[outOff + 2] = (byte)(b << 3);
                rgba[outOff + 3] = pixel == 0 ? (byte)0 : (byte)0xFF;
            }
        }

        return new Ps2Texture(entry.Checksum, tw, th,
            Ps2TexPixelDecoder.PSMCT16, 0, rgba);
    }

    private static byte[] ExpandPsmct16Palette(ReadOnlySpan<byte> clutBytes, int count)
    {
        var result = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            var pixel = BitConverter.ToUInt16(clutBytes[(i * 2)..]);
            var r = pixel & 0x1F;
            var g = (pixel >> 5) & 0x1F;
            var b = (pixel >> 10) & 0x1F;
            // PCSX2 expand: 5-bit channel << 3 = 8-bit value
            result[i * 4] = (byte)(r << 3);
            result[i * 4 + 1] = (byte)(g << 3);
            result[i * 4 + 2] = (byte)(b << 3);
            // PS2 PSMCT16 alpha: bit 15 = 1 means opaque, 0 means transparent
            // (unless AEM is set, but for texture CLUTs the standard convention
            // is: pixel==0 → fully transparent, otherwise → use TEXA.TA0/TA1)
            // For extraction, treat bit15=1 as opaque and bit15=0 as opaque too
            // UNLESS the entire pixel is 0x0000 (black + transparent).
            result[i * 4 + 3] = pixel == 0 ? (byte)0 : (byte)0xFF;
        }
        return result;
    }

    private static byte[] ExpandPsmct32Palette(ReadOnlySpan<byte> clutBytes, int count)
    {
        // PSMCT32 CLUT entries are stored as (R, G, B, A) per 4-byte entry.
        var result = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            result[i * 4] = clutBytes[i * 4];
            result[i * 4 + 1] = clutBytes[i * 4 + 1];
            result[i * 4 + 2] = clutBytes[i * 4 + 2];
            // PS2 GS alpha range is 0-128 (0x80 = fully opaque).
            // Scale to standard 0-255 range: min(alpha * 2, 255).
            var alpha = clutBytes[i * 4 + 3];
            result[i * 4 + 3] = (byte)Math.Min(alpha * 2, 255);
        }
        return result;
    }

}
