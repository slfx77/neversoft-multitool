using NeversoftMultitool.Core.Formats.Texture.Ps2;
using static NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex.ThawZoneTexFile;
using static NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex.ThawZoneTexTextureCache;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

internal static class ThawZoneTexHeaderUploadSupport
{
    internal static List<Ps2Texture> DecodeFromMatchedUploadBundles(
        IReadOnlyList<VramUpload> uploads,
        IReadOnlyList<ZoneTexHeaderEntry> headerEntries)
    {
        var textures = new List<Ps2Texture>();
        var decodedChecksums = new HashSet<uint>();

        foreach (var entry in headerEntries)
        {
            if (!decodedChecksums.Add(entry.Checksum))
                continue;

            var texture = DecodeFromMatchedUploadBundle(uploads, entry);
            if (texture?.Pixels != null)
                textures.Add(texture);
        }

        return textures;
    }

    internal static Ps2Texture? DecodeFromMatchedUploadBundle(
        IReadOnlyList<VramUpload> uploads,
        ZoneTexHeaderEntry entry)
    {
        var uploadIndex = FindUploadIndexForOffset(uploads, entry.UploadOffset);
        if (!uploadIndex.HasValue)
            return null;

        var upload = uploads[uploadIndex.Value];
        if (upload.SourceDataOffset != entry.UploadOffset && upload.RelativeDataOffset != entry.UploadOffset)
            return null;

        var tex0 = entry.Tex0;
        var psm = GetTex0Psm(tex0);
        var cpsm = GetTex0Cpsm(tex0);
        var csm = (uint)((tex0 >> 55) & 0x1);
        var width = 1 << (int)((tex0 >> 26) & 0xF);
        var height = 1 << (int)((tex0 >> 30) & 0xF);

        if (!Ps2TexPixelDecoder.IsValidPsm(psm))
            return null;

        var texData = TryDecodeBasePixels(upload, entry, width, height, psm);
        if (texData == null)
            return null;

        byte[]? clut = null;
        var paletteEntries = Ps2TexPixelDecoder.GetPaletteSize(psm);
        if (paletteEntries > 0)
        {
            clut = TryReadPaletteFromNearestUpload(uploads, uploadIndex.Value, tex0, psm, cpsm, csm);
            if (clut == null)
                clut = TryReadPaletteFromStreamingVram(uploads, uploadIndex.Value, tex0, psm, cpsm, csm);
            if (clut == null)
                return null;
        }

        var pixels = Ps2TexPixelDecoder.DecodePixels(texData, width, height, psm, cpsm, clut);
        return pixels == null
            ? null
            : new Ps2Texture(entry.Checksum, width, height, psm, cpsm, pixels);
    }

    private static byte[]? TryDecodeBasePixels(
        VramUpload upload,
        ZoneTexHeaderEntry entry,
        int width,
        int height,
        uint psm)
    {
        var inferredBytes = width * height * Ps2TexPixelDecoder.GetBitsPerPixel(psm) / 8;
        var basePixelBytes = entry.BasePixelBytes != 0 ? (int)entry.BasePixelBytes : inferredBytes;
        if (basePixelBytes <= 0 || upload.PixelData.Length < basePixelBytes)
            return null;

        var payload = upload.PixelData.AsSpan(0, basePixelBytes);

        return psm switch
        {
            Ps2TexPixelDecoder.PSMT4 => upload.Dpsm switch
            {
                Ps2TexPixelDecoder.PSMT4 => payload.ToArray(),
                Ps2TexPixelDecoder.PSMCT32 or Ps2TexPixelDecoder.PSMCT16
                    => Ps2TexSwizzle.UnswizzlePsmt4WithUploadDpsm(payload, width, height, upload.Dpsm),
                _ => null
            },
            Ps2TexPixelDecoder.PSMT8 => upload.Dpsm switch
            {
                Ps2TexPixelDecoder.PSMT8 => payload.ToArray(),
                Ps2TexPixelDecoder.PSMCT32 => Ps2TexSwizzle.UnswizzlePsmt8(payload, width, height),
                _ => null
            },
            Ps2TexPixelDecoder.PSMCT32 or Ps2TexPixelDecoder.PSMCT24 or Ps2TexPixelDecoder.PSMCT16
                => payload.ToArray(),
            _ => null
        };
    }

    private static byte[]? TryReadPaletteFromNearestUpload(
        IReadOnlyList<VramUpload> uploads,
        int matchedUploadIndex,
        ulong tex0,
        uint psm,
        uint cpsm,
        uint csm)
    {
        var cbp = GetTex0Cbp(tex0);
        var expectedWidth = psm == Ps2TexPixelDecoder.PSMT4 ? 8 : 16;
        var expectedHeight = psm == Ps2TexPixelDecoder.PSMT4 ? 2 : 16;

        for (var i = matchedUploadIndex - 1; i >= 0; i--)
        {
            var upload = uploads[i];
            if (upload.Dbp != cbp || upload.Dpsm != cpsm)
                continue;
            if (upload.Width != expectedWidth || upload.Height != expectedHeight)
                continue;

            var vram = new Ps2GsVram();
            vram.WriteRect(upload.Dbp, upload.Dbw, upload.Dpsm, upload.Width, upload.Height, upload.PixelData);
            return ReadPaletteFromVram(vram, tex0, psm, cpsm, csm);
        }

        return null;
    }

    private static byte[]? TryReadPaletteFromStreamingVram(
        IReadOnlyList<VramUpload> uploads,
        int matchedUploadIndex,
        ulong tex0,
        uint psm,
        uint cpsm,
        uint csm)
    {
        var vram = new Ps2GsVram();
        for (var i = 0; i < matchedUploadIndex; i++)
        {
            var upload = uploads[i];
            vram.WriteRect(upload.Dbp, upload.Dbw, upload.Dpsm, upload.Width, upload.Height, upload.PixelData);
        }

        return ReadPaletteFromVram(vram, tex0, psm, cpsm, csm);
    }

    private static byte[]? ReadPaletteFromVram(Ps2GsVram vram, ulong tex0, uint psm, uint cpsm, uint csm)
    {
        var cbp = GetTex0Cbp(tex0);
        byte[]? clut;
        if (psm == Ps2TexPixelDecoder.PSMT4 && csm == 0)
        {
            clut = ReadClutPsmt4Csm1(vram, cbp, cpsm);
        }
        else
        {
            var clutWidth = psm == Ps2TexPixelDecoder.PSMT4 ? 8 : 16;
            var clutHeight = psm == Ps2TexPixelDecoder.PSMT4 ? 2 : 16;
            clut = cpsm switch
            {
                Ps2TexPixelDecoder.PSMCT32 => vram.ReadRectPSMCT32(cbp, 1, clutWidth, clutHeight),
                Ps2TexPixelDecoder.PSMCT16 => vram.ReadRectPSMCT16(cbp, 1, clutWidth, clutHeight),
                _ => null
            };
        }

        if (clut == null)
            return null;

        if (psm == Ps2TexPixelDecoder.PSMT8)
        {
            var entrySize = Ps2TexPixelDecoder.GetBitsPerPixel(cpsm) / 8;
            if (entrySize > 0)
                ThawZoneTexVramSupport.UnswizzleClutCsm1(clut, entrySize);
        }

        return clut;
    }

    private static uint GetTex0Psm(ulong tex0)
    {
        return (uint)((tex0 >> 20) & 0x3F);
    }

    private static uint GetTex0Cbp(ulong tex0)
    {
        return (uint)((tex0 >> 37) & 0x3FFF);
    }

    private static uint GetTex0Cpsm(ulong tex0)
    {
        return (uint)((tex0 >> 51) & 0xF);
    }
}
