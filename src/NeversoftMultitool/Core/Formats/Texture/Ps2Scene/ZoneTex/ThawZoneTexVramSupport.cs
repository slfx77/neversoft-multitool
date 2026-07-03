using NeversoftMultitool.Core.Formats.Texture.Ps2;
using static NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex.ThawZoneTexFile;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

internal static class ThawZoneTexVramSupport
{
    internal static List<VramUpload> ParseVramUploads(ReadOnlySpan<byte> data)
    {
        var firstGif = FindFirstGifAdBlock(data);
        if (firstGif < 0)
            firstGif = 0;

        var descriptors = new List<VramUploadDescriptor>();
        for (var off = 0; off + 80 <= data.Length; off += 16)
        {
            var lo = BitConverter.ToUInt64(data[off..]);
            var hi = BitConverter.ToUInt64(data[(off + 8)..]);

            var nloop = (int)(lo & 0x7FFF);
            var flg = (int)((lo >> 58) & 3);
            var nreg = (int)((lo >> 60) & 0xF);
            if (nreg == 0)
                nreg = 16;

            if (flg == 0 && nreg == 1 && hi == 0x0E && nloop is >= 1 and <= 20)
            {
                var blockEnd = off + 16 + nloop * 16;
                if (blockEnd > data.Length)
                    continue;

                uint dbp = 0;
                uint dbw = 0;
                uint dpsm = 0;
                var rrw = 0;
                var rrh = 0;
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
                        case 0x50:
                            dbp = (uint)((regData >> 32) & 0x3FFF);
                            dbw = (uint)((regData >> 48) & 0x3F);
                            dpsm = (uint)((regData >> 56) & 0x3F);
                            hasBlt = true;
                            break;
                        case 0x52:
                            rrw = (int)(regData & 0xFFF);
                            rrh = (int)((regData >> 32) & 0xFFF);
                            hasTrx = true;
                            break;
                        case 0x53:
                            hasTrxDir = true;
                            break;
                    }
                }

                if (hasBlt && hasTrx && hasTrxDir && rrw > 0 && rrh > 0)
                {
                    var dataSize = GetTransferSizeBytes(dpsm, rrw, rrh);
                    var dataSizeAligned = (dataSize + 15) & ~15;
                    if (!TryReadImageTag(data, blockEnd, out var imageDataSize))
                        continue;

                    var dataOffset = blockEnd + 16;
                    if (imageDataSize >= dataSizeAligned && dataOffset + imageDataSize <= data.Length)
                    {
                        descriptors.Add(new VramUploadDescriptor(
                            dbp, dbw, dpsm, rrw, rrh,
                            dataOffset, imageDataSize, dataSize,
                            firstGif >= 0 ? (uint)(dataOffset - firstGif) : (uint)dataOffset));
                    }
                }
            }
        }

        var uploads = new List<VramUpload>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            var pixelData = ResolveUploadPixelData(data, descriptor, out var sourceDataOffset);
            uploads.Add(new VramUpload(
                descriptor.Dbp, descriptor.Dbw, descriptor.Dpsm,
                descriptor.Width, descriptor.Height,
                pixelData,
                descriptor.RelativeDataOffset,
                sourceDataOffset));
        }

        return uploads;
    }

    /// <summary>
    ///     Fetch and cook the CLUT for an indexed texture read: raw VRAM read at CBP with
    ///     the CPSM layout, plus the CSM1 unswizzle for 256-entry (PSMT8) palettes. This is
    ///     the exact palette bytes a decode consumes — callers snapshotting the GS on-chip
    ///     CLUT buffer at TEX0-write time store this cooked form.
    /// </summary>
    internal static byte[]? FetchClut(Ps2GsVram vram, uint psm, uint cbp, uint cpsm, uint csm)
    {
        byte[]? clut;
        if (psm == Ps2TexPixelDecoder.PSMT4 && csm == 0)
        {
            clut = ReadClutPsmt4Csm1(vram, cbp, cpsm);
        }
        else
        {
            var clutW = psm == Ps2TexPixelDecoder.PSMT4 ? 8 : 16;
            var clutH = psm == Ps2TexPixelDecoder.PSMT4 ? 2 : 16;
            clut = cpsm switch
            {
                Ps2TexPixelDecoder.PSMCT32 => vram.ReadRectPSMCT32(cbp, 1, clutW, clutH),
                Ps2TexPixelDecoder.PSMCT16 => vram.ReadRectPSMCT16(cbp, 1, clutW, clutH),
                _ => null
            };
        }

        if (clut != null && psm == Ps2TexPixelDecoder.PSMT8)
        {
            var clutBpp = Ps2TexPixelDecoder.GetBitsPerPixel(cpsm) / 8;
            if (clutBpp > 0)
                UnswizzleClutCsm1(clut, clutBpp);
        }

        return clut;
    }

    internal static byte[]? DecodeFromTex0(
        Ps2GsVram vram,
        ulong tex0,
        bool flipVertical = true,
        bool fixAllZeroAlpha = true,
        ulong? texa = null,
        byte[]? cookedClut = null,
        bool rawGsAlpha = false)
    {
        var tbp0 = (uint)(tex0 & 0x3FFF);
        var tbw = (uint)((tex0 >> 14) & 0x3F);
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var tw = 1 << (int)((tex0 >> 26) & 0xF);
        var th = 1 << (int)((tex0 >> 30) & 0xF);
        var cbp = (uint)((tex0 >> 37) & 0x3FFF);
        var cpsm = (uint)((tex0 >> 51) & 0xF);
        var csm = (uint)((tex0 >> 55) & 0x1);

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
                psm = Ps2TexPixelDecoder.PSMCT32;
                break;
            case Ps2GsVram.PSMZ32:
                texData = vram.ReadRectPSMZ32(tbp0, tbw, tw, th);
                psm = Ps2TexPixelDecoder.PSMCT32;
                break;
            case Ps2TexPixelDecoder.PSMCT24:
            {
                var raw32 = vram.ReadRectPSMCT32(tbp0, tbw, tw, th);
                texData = StripAlphaTo24(raw32, tw, th);
                psm = Ps2TexPixelDecoder.PSMCT24;
                break;
            }
            case Ps2GsVram.PSMZ24:
            {
                var raw32 = vram.ReadRectPSMZ24(tbp0, tbw, tw, th);
                texData = StripAlphaTo24(raw32, tw, th);
                psm = Ps2TexPixelDecoder.PSMCT24;
                break;
            }
            case Ps2TexPixelDecoder.PSMCT16:
                texData = vram.ReadRectPSMCT16(tbp0, tbw, tw, th);
                psm = Ps2TexPixelDecoder.PSMCT16;
                break;
            case Ps2GsVram.PSMCT16S:
                texData = vram.ReadRectPSMCT16S(tbp0, tbw, tw, th);
                psm = Ps2TexPixelDecoder.PSMCT16;
                break;
            case Ps2GsVram.PSMZ16:
                texData = vram.ReadRectPSMZ16(tbp0, tbw, tw, th);
                psm = Ps2TexPixelDecoder.PSMCT16;
                break;
            case Ps2GsVram.PSMZ16S:
                texData = vram.ReadRectPSMZ16S(tbp0, tbw, tw, th);
                psm = Ps2TexPixelDecoder.PSMCT16;
                break;
            default:
                return null;
        }

        byte[]? clut = null;
        var paletteSize = Ps2TexPixelDecoder.GetPaletteSize(psm);
        if (paletteSize > 0)
        {
            // Prefer a caller-provided cooked CLUT (the GS on-chip CLUT buffer snapshot
            // taken at TEX0-write time). Games time-multiplex palette VRAM: they upload a
            // palette, set TEX0 with CLD>=1 (the GS copies it on-chip), then overwrite the
            // same VRAM with other palettes before the draw kicks. Reading VRAM at
            // draw/decode time therefore fetches the WRONG palette (THAW shadow-decal
            // pool at 0x3590-0x359F).
            clut = cookedClut ?? FetchClut(vram, psm, cbp, cpsm, csm);
            if (clut == null)
                return null;
        }

        return Ps2TexPixelDecoder.DecodePixels(
            texData,
            tw,
            th,
            psm,
            cpsm,
            clut,
            flipVertical,
            fixAllZeroAlpha,
            texa,
            rawGsAlpha);
    }

    private static byte[] StripAlphaTo24(byte[] raw32, int tw, int th)
    {
        var texData = new byte[tw * th * 3];
        for (var src = 0; src < raw32.Length; src += 4)
        {
            var dst = src / 4 * 3;
            texData[dst] = raw32[src];
            texData[dst + 1] = raw32[src + 1];
            texData[dst + 2] = raw32[src + 2];
        }

        return texData;
    }

    internal static byte[]? ReadClutPsmt4Csm1(Ps2GsVram vram, uint cbp, uint cpsm)
    {
        return cpsm switch
        {
            Ps2TexPixelDecoder.PSMCT16 => vram.ReadRectPSMCT16(cbp, 1, 8, 2),
            Ps2TexPixelDecoder.PSMCT32 => vram.ReadRectPSMCT32(cbp, 1, 8, 2),
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
            if (nreg == 0)
                nreg = 16;

            if (flg != 0 || nreg != 1 || hi != 0x0E || nloop is < 3 or > 20)
                continue;

            var hasBlt = false;
            var hasTrx = false;
            for (var i = 0; i < nloop && off + 16 + (i + 1) * 16 <= data.Length; i++)
            {
                var regAddr = BitConverter.ToUInt64(data[(off + 16 + i * 16 + 8)..]);
                if (regAddr == 0x50)
                    hasBlt = true;
                if (regAddr == 0x52)
                    hasTrx = true;
            }

            if (!hasBlt || !hasTrx)
                continue;

            if (TryReadImageTag(data, off + 16 + nloop * 16, out _))
                return off;
        }

        return -1;
    }

    internal static Ps2GsVram BuildVram(
        IEnumerable<VramUpload> uploads,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var vram = new Ps2GsVram(gifQwordWordOrder ?? Ps2GifQwordWordOrder.Identity);
        foreach (var upload in uploads)
            vram.WriteRect(upload.Dbp, upload.Dbw, upload.Dpsm, upload.Width, upload.Height, upload.PixelData);

        return vram;
    }

    internal static int GetTransferSizeBytes(uint dpsm, int rrw, int rrh)
    {
        return dpsm switch
        {
            0 or 1 => rrw * rrh * 4,
            2 or 10 => rrw * rrh * 2,
            19 => rrw * rrh,
            20 => rrw * rrh / 2,
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

    private static byte[] ResolveUploadPixelData(
        ReadOnlySpan<byte> data,
        VramUploadDescriptor descriptor,
        out uint sourceDataOffset)
    {
        if (TryResolveRefUploadPixelData(data, descriptor, out var pixelData, out sourceDataOffset))
            return pixelData;

        sourceDataOffset = (uint)descriptor.ImageDataOffset;
        return data.Slice(descriptor.ImageDataOffset, descriptor.DataSize).ToArray();
    }

    private static bool TryResolveRefUploadPixelData(
        ReadOnlySpan<byte> data,
        VramUploadDescriptor descriptor,
        out byte[] pixelData,
        out uint sourceDataOffset)
    {
        pixelData = [];
        sourceDataOffset = 0;
        if (descriptor.ImageDataOffset + 16 > data.Length)
            return false;

        var tag = BitConverter.ToUInt32(data[descriptor.ImageDataOffset..]);
        var source = BitConverter.ToUInt32(data[(descriptor.ImageDataOffset + 4)..]);
        var zero = BitConverter.ToUInt32(data[(descriptor.ImageDataOffset + 8)..]);
        var endTag = BitConverter.ToUInt32(data[(descriptor.ImageDataOffset + 12)..]);

        var qwc = (descriptor.DataSize + 15) >> 4;
        if (tag >> 28 != 3 || endTag >> 28 != 5 || zero != 0)
            return false;
        if ((tag & 0xFFFF) != qwc || (endTag & 0xFFFF) != qwc)
            return false;

        var resolvedOffset = (int)(source & 0x00FFFFFF);
        if (resolvedOffset < 0 || resolvedOffset + descriptor.DataSize > data.Length)
            return false;

        pixelData = data.Slice(resolvedOffset, descriptor.DataSize).ToArray();
        sourceDataOffset = (uint)resolvedOffset;
        return true;
    }
}
