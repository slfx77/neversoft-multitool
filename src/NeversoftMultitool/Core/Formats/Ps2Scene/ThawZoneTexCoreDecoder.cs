using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexFile;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawZoneTexCoreDecoder
{
    internal static List<VramUpload> ParseVramUploads(ReadOnlySpan<byte> data)
    {
        var uploads = new List<VramUpload>();
        var firstGif = FindFirstGifAdBlock(data);
        var off = 0;

        while (off + 80 <= data.Length)
        {
            // Look for GIF A+D tag: FLG=0 (PACKED), NREG=1, REGS=0x0E
            var lo = BitConverter.ToUInt64(data[off..]);
            var hi = BitConverter.ToUInt64(data[(off + 8)..]);

            var nloop = (int)(lo & 0x7FFF);
            var flg = (int)((lo >> 58) & 3);
            var nreg = (int)((lo >> 60) & 0xF);
            if (nreg == 0) nreg = 16;

            if (flg == 0 && nreg == 1 && hi == 0x0E && nloop is >= 1 and <= 20)
            {
                var blockEnd = off + 16 + nloop * 16;
                if (blockEnd > data.Length)
                {
                    off += 16;
                    continue;
                }

                // Parse A+D register writes
                uint dbp = 0, dbw = 0, dpsm = 0;
                int rrw = 0, rrh = 0;
                var hasBlt = false;
                var hasTrx = false;
                var hasTrxDir = false;

                for (var i = 0; i < nloop; i++)
                {
                    var entryOff = off + 16 + i * 16;
                    var regData = BitConverter.ToUInt64(data[entryOff..]);
                    var regAddr = BitConverter.ToUInt64(data[(entryOff + 8)..]);

                    switch (regAddr)
                    {
                        case 0x50: // BITBLTBUF
                            dbp = (uint)((regData >> 32) & 0x3FFF);
                            dbw = (uint)((regData >> 48) & 0x3F);
                            dpsm = (uint)((regData >> 56) & 0x3F);
                            hasBlt = true;
                            break;
                        case 0x52: // TRXREG
                            rrw = (int)(regData & 0xFFF);
                            rrh = (int)((regData >> 32) & 0xFFF);
                            hasTrx = true;
                            break;
                        case 0x53: // TRXDIR
                            hasTrxDir = true;
                            break;
                    }
                }

                if (hasBlt && hasTrx && hasTrxDir && rrw > 0 && rrh > 0)
                {
                    var dataSize = GetTransferSizeBytes(dpsm, rrw, rrh);
                    var dataSizeAligned = (dataSize + 15) & ~15;
                    if (!TryReadImageTag(data, blockEnd, out var imageDataSize))
                    {
                        off = blockEnd;
                        continue;
                    }

                    var dataOffset = blockEnd + 16;
                    if (imageDataSize >= dataSizeAligned && dataOffset + imageDataSize <= data.Length)
                    {
                        uploads.Add(new VramUpload(
                            dbp, dbw, dpsm,
                            rrw, rrh,
                            data.Slice(dataOffset, dataSize).ToArray(),
                            firstGif >= 0 ? (uint)(dataOffset - firstGif) : (uint)dataOffset));

                        off = dataOffset + imageDataSize;
                        continue;
                    }
                }

                off = blockEnd;
                continue;
            }

            off += 16;
        }

        return uploads;
    }

    /// <summary>
    ///     Decode a texture from the VRAM simulator given a TEX0 register value.
    ///     Reads pixel data and CLUT from the VRAM buffer that was populated by
    ///     writing all zone TEX uploads using their transfer format's addressing.
    /// </summary>
    internal static byte[]? DecodeFromTex0(Ps2GsVram vram, ulong tex0)
    {
        var tbp0 = (uint)(tex0 & 0x3FFF);
        var tbw = (uint)((tex0 >> 14) & 0x3F);
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var tw = 1 << (int)((tex0 >> 26) & 0xF);
        var th = 1 << (int)((tex0 >> 30) & 0xF);
        var cbp = (uint)((tex0 >> 37) & 0x3FFF);
        var cpsm = (uint)((tex0 >> 51) & 0xF);
        var csm = (uint)((tex0 >> 55) & 0x1);

        // Read pixel data from VRAM using the texture's PSM addressing
        byte[] texData;
        switch (psm)
        {
            case Ps2TexPixelDecoder.PSMT4:
                texData = vram.ReadTexturePSMT4(tbp0, tbw, tw, th);
                break;
            case Ps2TexPixelDecoder.PSMT8:
                texData = vram.ReadTexturePSMT8(tbp0, tbw, tw, th);
                break;
            case Ps2TexPixelDecoder.PSMCT32:
                texData = vram.ReadRectPSMCT32(tbp0, tbw, tw, th);
                break;
            default:
                return null;
        }

        // For paletted formats, read CLUT from VRAM
        byte[]? clut = null;
        var paletteSize = Ps2TexPixelDecoder.GetPaletteSize(psm);
        if (paletteSize > 0)
        {
            if (psm == Ps2TexPixelDecoder.PSMT4 && csm == 0)
            {
                clut = ReadClutPsmt4Csm1(vram, cbp, cpsm);
            }
            else
            {
                // Read the palette back using the CLUT storage format from TEX0.CPSM.
                // PSMT8 still needs the standard 8-15 <-> 16-23 unswizzle per 32-entry group.
                var clutW = psm == Ps2TexPixelDecoder.PSMT4 ? 8 : 16;
                var clutH = psm == Ps2TexPixelDecoder.PSMT4 ? 2 : 16;
                clut = cpsm switch
                {
                    Ps2TexPixelDecoder.PSMCT32 => vram.ReadRectPSMCT32(cbp, 1, clutW, clutH),
                    Ps2TexPixelDecoder.PSMCT16 => vram.ReadRectPSMCT16(cbp, 1, clutW, clutH),
                    _ => null
                };
            }

            if (clut == null)
                return null;

            // Apply CSM1 CLUT unswizzle for PSMT8 (256 entries)
            if (psm == Ps2TexPixelDecoder.PSMT8)
            {
                var clutBpp = Ps2TexPixelDecoder.GetBitsPerPixel(cpsm) / 8;
                if (clutBpp > 0)
                    UnswizzleClutCsm1(clut, clutBpp);
            }
        }

        return Ps2TexPixelDecoder.DecodePixels(texData, tw, th, psm, cpsm, clut);
    }

    internal static byte[]? ReadClutPsmt4Csm1(Ps2GsVram vram, uint cbp, uint cpsm)
    {
        return cpsm switch
        {
            Ps2TexPixelDecoder.PSMCT16 => ReadClutPsmt4Csm1Ct16(vram, cbp),
            Ps2TexPixelDecoder.PSMCT32 => ReadClutPsmt4Csm1Ct32(vram, cbp),
            _ => null
        };
    }

    internal static int FindFirstGifAdBlock(ReadOnlySpan<byte> data)
    {
        for (var off = 0; off + 80 <= data.Length; off += 16)
        {
            var lo = BitConverter.ToUInt64(data[off..]);
            var hi = BitConverter.ToUInt64(data[(off + 8)..]);

            var nloop = (int)(lo & 0x7FFF);
            var flg = (int)((lo >> 58) & 3);
            var nreg = (int)((lo >> 60) & 0xF);
            if (nreg == 0) nreg = 16;

            if (flg != 0 || nreg != 1 || hi != 0x0E || nloop is < 3 or > 20)
                continue;

            // Verify it has BITBLTBUF + TRXREG
            var hasBlt = false;
            var hasTrx = false;
            for (var i = 0; i < nloop && off + 16 + (i + 1) * 16 <= data.Length; i++)
            {
                var regAddr = BitConverter.ToUInt64(data[(off + 16 + i * 16 + 8)..]);
                if (regAddr == 0x50) hasBlt = true;
                if (regAddr == 0x52) hasTrx = true;
            }

            if (!hasBlt || !hasTrx)
                continue;

            if (TryReadImageTag(data, off + 16 + nloop * 16, out _))
                return off;
        }

        return -1;
    }

    internal static int GetTransferSizeBytes(uint dpsm, int rrw, int rrh)
    {
        return dpsm switch
        {
            0 or 1 => rrw * rrh * 4, // PSMCT32, PSMCT24
            2 or 10 => rrw * rrh * 2, // PSMCT16, PSMCT16S
            19 => rrw * rrh, // PSMT8
            20 => rrw * rrh / 2, // PSMT4
            _ => rrw * rrh * 4
        };
    }

    internal static bool TryReadImageTag(ReadOnlySpan<byte> data, int offset, out int imageDataSize)
    {
        imageDataSize = 0;
        if (offset + 16 > data.Length)
            return false;

        var lo = BitConverter.ToUInt64(data[offset..]);
        var hi = BitConverter.ToUInt64(data[(offset + 8)..]);
        var flg = (int)((lo >> 58) & 3);
        if (flg != 2 || hi != 0)
            return false;

        imageDataSize = (int)(lo & 0x7FFF) * 16;
        return imageDataSize > 0;
    }

    internal static byte[] ReadClutPsmt4Csm1Ct16(Ps2GsVram vram, uint cbp)
    {
        var src = vram.ReadRawBlockHalfwords(cbp, 32);
        var clut = new byte[16 * 2];

        for (var i = 0; i < 16; i++)
        {
            var color = src[ClutTableT16I4[i]];
            clut[i * 2] = (byte)color;
            clut[i * 2 + 1] = (byte)(color >> 8);
        }

        return clut;
    }

    internal static byte[] ReadClutPsmt4Csm1Ct32(Ps2GsVram vram, uint cbp)
    {
        var src = vram.ReadRawBlockWords(cbp, 16);
        var clutBytes = new byte[16 * 4];
        for (var i = 0; i < ClutTableT32I4.Length; i++)
        {
            var word = src[ClutTableT32I4[i]];
            var off = i * 4;
            clutBytes[off] = (byte)word;
            clutBytes[off + 1] = (byte)(word >> 8);
            clutBytes[off + 2] = (byte)(word >> 16);
            clutBytes[off + 3] = (byte)(word >> 24);
        }

        return clutBytes;
    }

    /// <summary>
    ///     CSM1 CLUT unswizzle for PSMT8 (256 entries).
    ///     Identical to ThawSceneTexFile's version.
    /// </summary>
    internal static void UnswizzleClutCsm1(byte[] clut, int entrySize)
    {
        for (var group = 0; group < 8; group++)
        {
            for (var i = 0; i < 8; i++)
            {
                var posA = (group * 32 + 8 + i) * entrySize;
                var posB = (group * 32 + 16 + i) * entrySize;
                if (posA + entrySize > clut.Length || posB + entrySize > clut.Length)
                    return;
                for (var j = 0; j < entrySize; j++)
                    (clut[posA + j], clut[posB + j]) = (clut[posB + j], clut[posA + j]);
            }
        }
    }

    /// <summary>
    ///     Populate a GS VRAM simulator with all zone TEX upload data.
    ///     Each upload is written to VRAM using the transfer format's (DPSM) addressing,
    ///     which may differ from the texture read format (PSM). This is critical because
    ///     PS2 commonly uploads PSMT4 data as PSMCT32 for DMA efficiency.
    /// </summary>
    internal static Ps2GsVram BuildVram(IEnumerable<VramUpload> uploads,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var vram = new Ps2GsVram(gifQwordWordOrder ?? Ps2GifQwordWordOrder.Identity);
        foreach (var upload in uploads)
            vram.WriteRect(upload.Dbp, upload.Dbw, upload.Dpsm,
                upload.Width, upload.Height, upload.PixelData);
        return vram;
    }

    /// <summary>
    ///     Decode textures from VRAM uploads using TEX0 register values from companion MDL files.
    ///     The TEX0 values specify the actual texture format, dimensions, and CLUT address —
    ///     without them, the transfer format (DPSM) and dimensions don't reliably indicate
    ///     the real texture parameters (e.g. PSMT4 data may be uploaded as PSMCT32).
    ///     For paletted textures, decoding prefers the first upload-stream snapshot where the
    ///     relevant CLUT base has been written, rather than the final merged VRAM state.
    /// </summary>
}
