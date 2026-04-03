using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexFile;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawZoneTexTextureCache
{
    internal static List<TextureDecodeRequest> BuildDecodeRequests(
        IReadOnlyList<VramUpload> uploads, IEnumerable<ulong> tex0Values,
        IReadOnlyDictionary<(uint Tbp, uint Cbp), uint>? checksumMap)
    {
        var exactBases = uploads.Select(static upload => upload.Dbp).ToHashSet();
        var requests = new List<TextureDecodeRequest>();
        var decoded = new HashSet<uint>();

        foreach (var tex0 in tex0Values)
        {
            var tbp = (uint)(tex0 & 0x3FFF);
            var cbp = (uint)((tex0 >> 37) & 0x3FFF);
            var psm = (uint)((tex0 >> 20) & 0x3F);
            var tw = 1 << (int)((tex0 >> 26) & 0xF);
            var th = 1 << (int)((tex0 >> 30) & 0xF);
            var cpsm = (uint)((tex0 >> 51) & 0xF);

            var checksum = checksumMap != null && checksumMap.TryGetValue((tbp, cbp), out var headerChecksum)
                ? headerChecksum
                : (tbp << 16) | cbp;
            if (checksum == 0 || !decoded.Add(checksum))
                continue;

            requests.Add(new TextureDecodeRequest(
                tex0,
                checksum,
                tbp,
                cbp,
                tw,
                th,
                psm,
                cpsm,
                Ps2TexPixelDecoder.GetPaletteSize(psm) > 0,
                exactBases.Contains(tbp),
                exactBases.Contains(cbp),
                null));
        }

        return requests;
    }

    internal static List<TextureDecodeRequest> BuildDecodeRequests(
        IReadOnlyList<VramUpload> uploads, IEnumerable<ZoneTexHeaderEntry> headerEntries)
    {
        var exactBases = uploads.Select(static upload => upload.Dbp).ToHashSet();
        var requests = new List<TextureDecodeRequest>();
        var decoded = new HashSet<uint>();

        foreach (var entry in headerEntries)
        {
            if (!decoded.Add(entry.Checksum))
                continue;

            var tex0 = entry.Tex0;
            var tbp = (uint)(tex0 & 0x3FFF);
            var cbp = (uint)((tex0 >> 37) & 0x3FFF);
            var psm = (uint)((tex0 >> 20) & 0x3F);
            var tw = 1 << (int)((tex0 >> 26) & 0xF);
            var th = 1 << (int)((tex0 >> 30) & 0xF);
            var cpsm = (uint)((tex0 >> 51) & 0xF);

            requests.Add(new TextureDecodeRequest(
                tex0,
                entry.Checksum,
                tbp,
                cbp,
                tw,
                th,
                psm,
                cpsm,
                Ps2TexPixelDecoder.GetPaletteSize(psm) > 0,
                exactBases.Contains(tbp),
                exactBases.Contains(cbp),
                FindUploadIndexForOffset(uploads, entry.UploadOffset)));
        }

        return requests;
    }

    internal static Dictionary<uint, Ps2Texture> DecodeTextureCache(
        IReadOnlyList<VramUpload> uploads, IReadOnlyList<TextureDecodeRequest> requests,
        Ps2GifQwordWordOrder? gifQwordWordOrder)
    {
        var textureCache = new Dictionary<uint, Ps2Texture>();
        if (requests.Count == 0)
            return textureCache;

        var requestsByTbp = new Dictionary<uint, List<int>>();
        var requestsByCbp = new Dictionary<uint, List<int>>();
        var requestsByTargetUpload = new Dictionary<uint, List<int>>();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            AddRequestIndex(requestsByTbp, request.Tbp, i);
            if (request.NeedsPalette)
                AddRequestIndex(requestsByCbp, request.Cbp, i);
            if (request.TargetUploadIndex.HasValue)
                AddRequestIndex(requestsByTargetUpload, (uint)request.TargetUploadIndex.Value, i);
        }

        var seenTbp = new HashSet<uint>();
        var seenCbp = new HashSet<uint>();
        var vram = new Ps2GsVram(gifQwordWordOrder ?? Ps2GifQwordWordOrder.Identity);

        for (var uploadIndex = 0; uploadIndex < uploads.Count; uploadIndex++)
        {
            var upload = uploads[uploadIndex];
            vram.WriteRect(upload.Dbp, upload.Dbw, upload.Dpsm, upload.Width, upload.Height, upload.PixelData);

            requestsByTbp.TryGetValue(upload.Dbp, out var tbpRequests);
            requestsByCbp.TryGetValue(upload.Dbp, out var cbpRequests);
            requestsByTargetUpload.TryGetValue((uint)uploadIndex, out var targetRequests);
            if (tbpRequests == null && cbpRequests == null && targetRequests == null)
                continue;

            if (tbpRequests != null)
                seenTbp.Add(upload.Dbp);

            if (cbpRequests != null)
                seenCbp.Add(upload.Dbp);

            if (tbpRequests != null)
                foreach (var requestIndex in tbpRequests)
                    TryResolveRequest(textureCache, requests[requestIndex], vram, seenTbp, seenCbp, uploadIndex);

            if (cbpRequests != null)
                foreach (var requestIndex in cbpRequests)
                    TryResolveRequest(textureCache, requests[requestIndex], vram, seenTbp, seenCbp, uploadIndex);

            if (targetRequests != null)
                foreach (var requestIndex in targetRequests)
                    TryResolveRequest(textureCache, requests[requestIndex], vram, seenTbp, seenCbp, uploadIndex);
        }

        foreach (var request in requests)
            TryResolveFallback(textureCache, request, vram);

        return textureCache;
    }

    internal static void AddRequestIndex(Dictionary<uint, List<int>> requestMap, uint bp, int requestIndex)
    {
        if (!requestMap.TryGetValue(bp, out var indexes))
        {
            indexes = [];
            requestMap[bp] = indexes;
        }

        indexes.Add(requestIndex);
    }

    internal static void TryResolveRequest(
        Dictionary<uint, Ps2Texture> textureCache, TextureDecodeRequest request, Ps2GsVram vram,
        IReadOnlySet<uint> seenTbp, IReadOnlySet<uint> seenCbp, int currentUploadIndex)
    {
        if (textureCache.ContainsKey(request.Checksum))
            return;

        if (!IsReadyForSnapshot(request, seenTbp, seenCbp, currentUploadIndex))
            return;

        var pixels = DecodeFromTex0(vram, request.Tex0);
        if (pixels == null)
            return;

        textureCache[request.Checksum] = new Ps2Texture(
            request.Checksum, request.Width, request.Height, request.Psm, request.Cpsm, pixels);
    }

    internal static void TryResolveFallback(
        Dictionary<uint, Ps2Texture> textureCache, TextureDecodeRequest request, Ps2GsVram vram)
    {
        if (textureCache.ContainsKey(request.Checksum))
            return;

        var pixels = DecodeFromTex0(vram, request.Tex0);
        if (pixels == null)
            return;

        textureCache[request.Checksum] = new Ps2Texture(
            request.Checksum, request.Width, request.Height, request.Psm, request.Cpsm, pixels);
    }

    internal static bool IsReadyForSnapshot(
        TextureDecodeRequest request, IReadOnlySet<uint> seenTbp, IReadOnlySet<uint> seenCbp, int currentUploadIndex)
    {
        if (request.TargetUploadIndex.HasValue && currentUploadIndex < request.TargetUploadIndex.Value)
            return false;

        if (!request.NeedsPalette)
            return request.TargetUploadIndex.HasValue || seenTbp.Contains(request.Tbp);

        if (request.TargetUploadIndex.HasValue)
            return !request.HasExactCbpUpload || seenCbp.Contains(request.Cbp);

        if (!request.HasExactCbpUpload || !seenCbp.Contains(request.Cbp))
            return false;

        return !request.HasExactTbpUpload || seenTbp.Contains(request.Tbp);
    }

    internal static int? FindUploadIndexForOffset(IReadOnlyList<VramUpload> uploads, uint uploadOffset)
    {
        for (var i = 0; i < uploads.Count; i++)
        {
            if (uploads[i].SourceDataOffset == uploadOffset || uploads[i].RelativeDataOffset == uploadOffset)
                return i;
        }

        for (var i = 0; i < uploads.Count; i++)
        {
            var upload = uploads[i];
            if (UploadContainsOffset(upload.SourceDataOffset, upload.PixelData.Length, uploadOffset) ||
                UploadContainsOffset(upload.RelativeDataOffset, upload.PixelData.Length, uploadOffset))
                return i;
        }

        return null;
    }

    private static bool UploadContainsOffset(uint uploadStart, int pixelDataLength, uint uploadOffset)
    {
        if (uploadStart == 0 || pixelDataLength <= 0)
            return false;

        var uploadEnd = uploadStart + (uint)pixelDataLength;
        return uploadOffset > uploadStart && uploadOffset < uploadEnd;
    }
}
