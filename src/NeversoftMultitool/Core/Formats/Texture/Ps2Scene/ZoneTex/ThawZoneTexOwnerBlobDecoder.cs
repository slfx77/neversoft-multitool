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
///         - <c>relocated_cumul_off</c> points at the prepared pixel data (PSMT4 nibbles
///           pre-swizzled in the GS Conv4to32 layout, or PSMT8 bytes for paletted-8).
///
///     4. Decode (per FUN_0019cd48):
///         - For PSMT4: pixels are stored in PSMCT32-uploaded layout. Apply Conv4to32
///           unswizzle, then look up each nibble in the CLUT, BOTTOM-UP scan.
///         - For PSMT8: similar but with CSM1 lookup table on the index.
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

        // Scan for a header that ends exactly at dmaStart
        for (var off = 0x100; off + 0x10 < dmaStart; off += 4)
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

        if (psm != Ps2TexPixelDecoder.PSMT4 && psm != Ps2TexPixelDecoder.PSMT8)
            return null; // This owner-blob path handles only PSMT4 / PSMT8 entries.
        if (entry.PaletteBytes == 0 || entry.DataSize == 0)
            return null;

        // Relocate per FUN_001e9ac0
        var pixelAbs = (long)headerBase + entry.CumulativeOffset + baseB;
        var clutAbs = (long)headerBase + entry.DataOffset + baseB;
        var pixelSize = psm == Ps2TexPixelDecoder.PSMT4 ? (long)tw * th / 2 : (long)tw * th;

        if (pixelAbs < 0 || pixelAbs + pixelSize > fileData.Length)
            return null;
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

        // Note: NO CSM1 unswizzle needed here. In zone .tex files the CLUT is stored
        // in linear order and the pixel indices are already pre-swizzled by the build tool.
        // Applying CSM1 would double-swizzle and produce wrong colors.

        // Unswizzle pixel data.
        //   PSMT4: stored in PSMCT32-uploaded layout (Conv4to32). Apply UnswizzlePsmt4.
        //   PSMT8: stored in Conv8to32 layout. The data_size is tw*th (the PSMT8 byte
        //          count), while the upload was PSMCT32 at half-width × half-height.
        //          UnswizzlePsmt8 handles this mapping correctly.
        byte[] unswizzled;
        if (psm == Ps2TexPixelDecoder.PSMT4)
            unswizzled = Ps2TexSwizzle.UnswizzlePsmt4(pixelBytes, tw, th);
        else
            unswizzled = Ps2TexSwizzle.UnswizzlePsmt8(pixelBytes, tw, th);

        // Convert linear index buffer to RGBA32, BOTTOM-UP scan
        var rgba = new byte[tw * th * 4];
        for (var y = 0; y < th; y++)
        {
            var srcY = th - 1 - y;
            for (var x = 0; x < tw; x++)
            {
                int idx;
                if (psm == Ps2TexPixelDecoder.PSMT4)
                {
                    var nibblePos = srcY * tw + x;
                    var byteIdx = nibblePos / 2;
                    var shift = (nibblePos & 1) * 4;
                    idx = (unswizzled[byteIdx] >> shift) & 0xF;
                }
                else
                {
                    idx = unswizzled[srcY * tw + x];
                }
                var paletteOff = idx * 4;
                var outOff = (y * tw + x) * 4;
                rgba[outOff] = palette[paletteOff];
                rgba[outOff + 1] = palette[paletteOff + 1];
                rgba[outOff + 2] = palette[paletteOff + 2];
                rgba[outOff + 3] = palette[paletteOff + 3];
            }
        }

        return new Ps2Texture(entry.Checksum, tw, th, psm, cpsm, rgba);
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
