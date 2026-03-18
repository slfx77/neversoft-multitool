using System.Numerics;
using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parser for THAW PS2 world-zone TEX files extracted from PAK archives.
///     Unlike version-6 scene tex files (ThawSceneTexFile), these files contain
///     raw GIF A+D register blocks that upload texture data to PS2 GS VRAM.
///     The MDL companion file references textures by TEX0 VRAM addresses (TBP/CBP),
///     not by checksums. This class builds a VRAM map from the uploads, then
///     decodes textures on demand given a TEX0 register value.
/// </summary>
public static class ThawZoneTexFile
{
    internal static readonly int[] ClutTableT16I4 =
    {
        0, 2, 8, 10, 16, 18, 24, 26,
        4, 6, 12, 14, 20, 22, 28, 30
    };

    internal static readonly int[] ClutTableT32I4 =
    {
        0, 1, 4, 5, 8, 9, 12, 13,
        2, 3, 6, 7, 10, 11, 14, 15
    };

    internal static readonly int[] Layout02000001BlockPermutation =
    [
        0, 3, 1, 2, 4
    ];

    internal static readonly int[] Layout02000005BlockPermutation =
    [
        0, 2, 1, 3, 4
    ];

    internal static readonly int[] Layout02000005TilePermutation =
    [
        1, 5, 0, 4, 2, 3
    ];

    /// <summary>
    ///     Detect world-zone TEX files. These start with a non-zero u32 checksum
    ///     (not version 6), contain no standard TEX header, and have GIF A+D blocks.
    /// </summary>
    public static bool IsThawZoneTex(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x200) return false;

        // Must NOT be a standard format
        var version = BitConverter.ToUInt16(data);
        if (version is >= 2 and <= 6) return false;

        // Scan for at least one GIF A+D block with BITBLTBUF
        return FindFirstGifAdBlock(data) >= 0;
    }

    /// <summary>
    ///     Parse the TEX file and build both a texture cache (synthetic checksum -> decoded pixels)
    ///     and a TEX0 mapping ((Group, TBP, CBP) -> synthetic checksum).
    ///     The synthetic checksum is derived from TBP+CBP so the existing pipeline can match.
    /// </summary>
    public static ZoneTexResult Parse(ReadOnlySpan<byte> data)
    {
        var uploads = ParseVramUploads(data);
        return new ZoneTexResult(uploads);
    }

    /// <summary>
    ///     Parse the fixed-size texture metadata entries that precede the GIF upload stream.
    ///     These entries carry the real texture checksums and TEX0 values used by zone MDLs.
    /// </summary>
    public static List<ZoneTexHeaderEntry> ParseHeaderEntries(ReadOnlySpan<byte> data)
    {
        return ThawZoneTexHeaderParser.ParseHeaderEntries(data);
    }

    /// <summary>
    ///     Parse all GIF A+D upload blocks from the file.
    ///     Each upload records the VRAM destination (DBP), pixel format, dimensions,
    ///     and the raw pixel data bytes.
    /// </summary>

    internal static List<VramUpload> ParseVramUploads(ReadOnlySpan<byte> data)
    {
        return ThawZoneTexCoreDecoder.ParseVramUploads(data);
    }

    internal static byte[]? DecodeFromTex0(Ps2GsVram vram, ulong tex0)
    {
        return ThawZoneTexCoreDecoder.DecodeFromTex0(vram, tex0);
    }

    internal static byte[]? ReadClutPsmt4Csm1(Ps2GsVram vram, uint cbp, uint cpsm)
    {
        return ThawZoneTexCoreDecoder.ReadClutPsmt4Csm1(vram, cbp, cpsm);
    }


    /// <summary>
    ///     Build a texture provider and TEX0 mapping for integration with the existing pipeline.
    ///     Creates synthetic checksums from TBP+CBP pairs.
    /// </summary>
    public static (Dictionary<uint, Ps2Texture> TextureCache,
        Dictionary<(uint Group, uint Tbp, uint Cbp), uint> Tex0Map)
        BuildMappings(IReadOnlyList<VramUpload> uploads, IEnumerable<ulong>? knownTex0Values = null,
            IReadOnlyDictionary<(uint Tbp, uint Cbp), uint>? checksumMap = null,
            Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var textureCache = new Dictionary<uint, Ps2Texture>();
        var tex0Map = new Dictionary<(uint Group, uint Tbp, uint Cbp), uint>();

        if (knownTex0Values == null)
            return (textureCache, tex0Map);

        var requests = BuildDecodeRequests(uploads, knownTex0Values, checksumMap);
        textureCache = DecodeTextureCache(uploads, requests, gifQwordWordOrder);

        foreach (var request in requests)
            tex0Map.TryAdd((0, request.Tbp, request.Cbp), request.Checksum);

        return (textureCache, tex0Map);
    }


    internal static int FindFirstGifAdBlock(ReadOnlySpan<byte> data)
    {
        return ThawZoneTexCoreDecoder.FindFirstGifAdBlock(data);
    }

    internal static Ps2GsVram BuildVram(IEnumerable<VramUpload> uploads,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        return ThawZoneTexCoreDecoder.BuildVram(uploads, gifQwordWordOrder);
    }

    public static List<Ps2Texture> DecodeFromTex0Values(
        IReadOnlyList<VramUpload> uploads, IEnumerable<ulong> tex0Values,
        IReadOnlyDictionary<(uint Tbp, uint Cbp), uint>? checksumMap = null,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var requests = BuildDecodeRequests(uploads, tex0Values, checksumMap);
        var textureCache = DecodeTextureCache(uploads, requests, gifQwordWordOrder);

        var textures = new List<Ps2Texture>();
        foreach (var request in requests)
            if (textureCache.TryGetValue(request.Checksum, out var texture))
                textures.Add(texture);

        return textures;
    }

    public static List<Ps2Texture> DecodeFromHeaderEntries(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        IEnumerable<ZoneTexHeaderEntry> headerEntries,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var headerList = headerEntries.ToList();
        var textures = DecodeFromHeaderDataSlots(fileData, uploads, headerList);
        if (textures.Count == headerList.Count)
            return textures;

        var decodedChecksums = textures.Select(static texture => texture.Checksum).ToHashSet();
        var unresolvedHeaders = headerList
            .Where(entry => !decodedChecksums.Contains(entry.Checksum))
            .ToList();
        if (unresolvedHeaders.Count == 0)
            return textures;

        foreach (var texture in DecodeFromHeaderEntries(uploads, unresolvedHeaders, gifQwordWordOrder))
            if (decodedChecksums.Add(texture.Checksum))
                textures.Add(texture);

        return textures;
    }

    public static List<Ps2Texture> DecodeFromHeaderEntries(
        IReadOnlyList<VramUpload> uploads, IEnumerable<ZoneTexHeaderEntry> headerEntries,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var requests = BuildDecodeRequests(uploads, headerEntries);
        var textureCache = DecodeTextureCache(uploads, requests, gifQwordWordOrder);

        var textures = new List<Ps2Texture>();
        foreach (var request in requests)
            if (textureCache.TryGetValue(request.Checksum, out var texture))
                textures.Add(texture);

        return textures;
    }

    internal static List<Ps2Texture> DecodeFromHeaderDataSlots(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        IReadOnlyList<ZoneTexHeaderEntry> headerEntries)
    {
        var textures = new List<Ps2Texture>();
        if (headerEntries.Count == 0)
            return textures;

        if (!TryGetHeaderDataLayout(fileData, out var dataBaseOffset, out var dataOffsetBias))
            return textures;

        var sameCbpClutSources = BuildSameCbpClutSourceMap(fileData, dataBaseOffset, dataOffsetBias, headerEntries);
        var decodedChecksums = new HashSet<uint>();
        foreach (var entry in headerEntries)
        {
            if (!decodedChecksums.Add(entry.Checksum))
                continue;

            var texture = DecodeBestHeaderDataSlot(
                fileData,
                uploads,
                dataBaseOffset,
                dataOffsetBias,
                entry,
                sameCbpClutSources);
            if (texture?.Pixels != null)
                textures.Add(texture);
        }

        return textures;
    }

    internal static bool TryGetHeaderDataLayout(ReadOnlySpan<byte> fileData, out int dataBaseOffset,
        out int dataOffsetBias)
    {
        dataBaseOffset = 0;
        dataOffsetBias = 0;

        var entries = ParseHeaderEntries(fileData);
        var maxEnd = 0L;

        foreach (var entry in entries)
        {
            var slotEnd = (long)entry.DataOffset + entry.PaletteBytes + entry.DataSize;
            if (slotEnd <= fileData.Length)
                maxEnd = Math.Max(maxEnd, slotEnd);
        }

        if (maxEnd <= 0 || maxEnd > fileData.Length)
            return false;

        dataBaseOffset = (int)(fileData.Length - maxEnd);
        dataOffsetBias = (int)entries
            .Where(static entry => entry.DataOffset == 0)
            .Select(static entry => entry.PaletteBytes)
            .DefaultIfEmpty((uint)0)
            .Max();
        return dataBaseOffset >= 0;
    }


    private static Ps2Texture? DecodeBestHeaderDataSlot(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        int dataBaseOffset,
        int dataOffsetBias,
        ZoneTexHeaderEntry entry,
        IReadOnlyDictionary<ZoneTexHeaderEntry, HeaderClutSourceContext>? sameCbpClutSources = null)
    {
        return ThawZoneTexHeaderDataSupport.DecodeBestHeaderDataSlot(
            fileData, uploads, dataBaseOffset, dataOffsetBias, entry, sameCbpClutSources);
    }

    private static Dictionary<ZoneTexHeaderEntry, HeaderClutSourceContext> BuildSameCbpClutSourceMap(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        int dataOffsetBias,
        IReadOnlyList<ZoneTexHeaderEntry> headerEntries)
    {
        return ThawZoneTexHeaderDataSupport.BuildSameCbpClutSourceMap(
            fileData, dataBaseOffset, dataOffsetBias, headerEntries);
    }

    internal static int? SelectHeaderDataSlotBias(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        int dataOffsetBias,
        ZoneTexHeaderEntry entry)
    {
        return ThawZoneTexHeaderDataSupport.SelectHeaderDataSlotBias(fileData, dataBaseOffset, dataOffsetBias, entry);
    }

    internal static bool ShouldPreferSameCbpEntropyClut(ZoneTexHeaderEntry entry)
    {
        return ThawZoneTexHeaderDataSupport.ShouldPreferSameCbpEntropyClut(entry);
    }

    internal static double ComputeHeaderClutEntropy(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        ZoneTexHeaderEntry entry,
        int selectedBias)
    {
        return ThawZoneTexHeaderDataSupport.ComputeHeaderClutEntropy(fileData, dataBaseOffset, entry, selectedBias);
    }

    internal static byte[] ReorderClutPsmt4ForLayout(byte[] clut, uint cpsm, uint layoutMode)
    {
        return ThawZoneTexHeaderDataSupport.ReorderClutPsmt4ForLayout(clut, cpsm, layoutMode);
    }

    internal static byte[] TransformPsmt4LinearBlocks(
        ReadOnlySpan<byte> texData,
        int width,
        int height,
        int blockWidthPixels,
        int blockHeightPixels,
        IReadOnlyList<int> bitPermutation,
        int startPixelIndex)
    {
        return ThawZoneTexHeaderLayoutSupport.TransformPsmt4LinearBlocks(
            texData, width, height, blockWidthPixels, blockHeightPixels, bitPermutation, startPixelIndex);
    }

    internal static byte[] TransformPsmt4SlotBlocks(
        ReadOnlySpan<byte> texData,
        int width,
        int height,
        IReadOnlyList<int> bitPermutation,
        int startPixelIndex)
    {
        return ThawZoneTexHeaderLayoutSupport.TransformPsmt4SlotBlocks(
            texData, width, height, bitPermutation, startPixelIndex);
    }

    internal static byte[] TransformPsmt4SlotBlocksForLayout(
        ReadOnlySpan<byte> texData,
        int width,
        int height,
        uint layoutMode)
    {
        return ThawZoneTexHeaderLayoutSupport.TransformPsmt4SlotBlocksForLayout(texData, width, height, layoutMode);
    }

    internal static bool ShouldApplyPsmt4SlotLayoutTransform(uint layoutMode, int selectedBias)
    {
        return ThawZoneTexHeaderLayoutSupport.ShouldApplyPsmt4SlotLayoutTransform(layoutMode, selectedBias);
    }

    internal static bool ShouldApplyPsmt4SlotTileTransform(
        ZoneTexHeaderEntry entry,
        int selectedBias,
        VramUpload? matchedUpload)
    {
        return ThawZoneTexHeaderLayoutSupport.ShouldApplyPsmt4SlotTileTransform(entry, selectedBias, matchedUpload);
    }

    internal static bool ShouldPreferPsmt4BiasedAutoSlotCandidate(
        ZoneTexHeaderEntry entry,
        int selectedBias,
        VramUpload? matchedUpload)
    {
        return ThawZoneTexHeaderLayoutSupport.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry, selectedBias, matchedUpload);
    }

    internal static bool ShouldPreferNobiasForBias32Bucket(
        ZoneTexHeaderEntry entry,
        int selectedBias,
        VramUpload? matchedUpload)
    {
        return ThawZoneTexHeaderLayoutSupport.ShouldPreferNobiasForBias32Bucket(entry, selectedBias, matchedUpload);
    }

    internal static byte[] ReorderClut(byte[] clut, int[] table, int entrySize)
    {
        return ThawZoneTexHeaderLayoutSupport.ReorderClut(clut, table, entrySize);
    }

    private static List<TextureDecodeRequest> BuildDecodeRequests(
        IReadOnlyList<VramUpload> uploads, IEnumerable<ulong> tex0Values,
        IReadOnlyDictionary<(uint Tbp, uint Cbp), uint>? checksumMap)
    {
        return ThawZoneTexTextureCache.BuildDecodeRequests(uploads, tex0Values, checksumMap);
    }

    private static List<TextureDecodeRequest> BuildDecodeRequests(
        IReadOnlyList<VramUpload> uploads, IEnumerable<ZoneTexHeaderEntry> headerEntries)
    {
        return ThawZoneTexTextureCache.BuildDecodeRequests(uploads, headerEntries);
    }

    private static Dictionary<uint, Ps2Texture> DecodeTextureCache(
        IReadOnlyList<VramUpload> uploads, IReadOnlyList<TextureDecodeRequest> requests,
        Ps2GifQwordWordOrder? gifQwordWordOrder)
    {
        return ThawZoneTexTextureCache.DecodeTextureCache(uploads, requests, gifQwordWordOrder);
    }

    public static List<MdlGsTextureState> ExtractTextureStatesFromMdl(byte[] mdlData)
    {
        return ThawZoneTexMdlSupport.ExtractTextureStatesFromMdl(mdlData);
    }

    /// <summary>
    ///     Extract all unique TEX0 register values from a PAK MDL file's GS context blocks.
    /// </summary>
    public static HashSet<ulong> ExtractTex0ValuesFromMdl(byte[] mdlData)
    {
        return ThawZoneTexMdlSupport.ExtractTex0ValuesFromMdl(mdlData);
    }


    public static Dictionary<(uint Tbp, uint Cbp), uint> BuildChecksumMapFromHeaders(
        IEnumerable<ReadOnlyMemory<byte>> fileData)
    {
        return ThawZoneTexHeaderSourceResolver.BuildChecksumMapFromHeaders(fileData);
    }

    public static Dictionary<ulong, ZoneTexHeaderSourceEntry> BuildHeaderSourceEntryMapByTex0FromHeaderLists(
        IEnumerable<IReadOnlyList<ZoneTexHeaderEntry>> headerLists)
    {
        return ThawZoneTexHeaderSourceResolver.BuildHeaderSourceEntryMapByTex0FromHeaderLists(headerLists);
    }

    public static Dictionary<(uint Tbp, uint Cbp), List<ZoneTexHeaderSourceEntry>>
        BuildHeaderSourceEntryGroupsFromHeaderLists(
            IEnumerable<IReadOnlyList<ZoneTexHeaderEntry>> headerLists)
    {
        return ThawZoneTexHeaderSourceResolver.BuildHeaderSourceEntryGroupsFromHeaderLists(headerLists);
    }

    public static bool TryResolveHeaderSourceEntry(
        ulong tex0,
        ulong tex1,
        IReadOnlyDictionary<ulong, ZoneTexHeaderSourceEntry> exactMap,
        IReadOnlyDictionary<(uint Tbp, uint Cbp), List<ZoneTexHeaderSourceEntry>> candidateGroups,
        out ZoneTexHeaderSourceEntry resolved)
    {
        return ThawZoneTexHeaderSourceResolver.TryResolveHeaderSourceEntry(
            tex0, tex1, exactMap, candidateGroups, out resolved);
    }

    public static Dictionary<(uint Tbp, uint Cbp), int> BuildSourceIndexMapFromHeaderLists(
        IEnumerable<IReadOnlyList<ZoneTexHeaderEntry>> headerLists)
    {
        return ThawZoneTexHeaderSourceResolver.BuildSourceIndexMapFromHeaderLists(headerLists);
    }

    public static Dictionary<(uint Tbp, uint Cbp), ZoneTexHeaderEntry> BuildHeaderEntryMapFromHeaderLists(
        IEnumerable<IReadOnlyList<ZoneTexHeaderEntry>> headerLists)
    {
        return ThawZoneTexHeaderSourceResolver.BuildHeaderEntryMapFromHeaderLists(headerLists);
    }

    public readonly record struct VramUpload(
        uint Dbp,
        uint Dbw,
        uint Dpsm,
        int Width,
        int Height,
        byte[] PixelData,
        uint RelativeDataOffset = 0);

    /// <summary>Result from parsing a zone TEX file.</summary>
    public sealed class ZoneTexResult(List<VramUpload> uploads)
    {
        public List<VramUpload> Uploads { get; } = uploads;
        public int UploadCount => Uploads.Count;
    }

    public readonly record struct MdlGsTextureState(
        ulong Tex0,
        ulong Tex1,
        ulong MipTbp1,
        ulong MipTbp2);

    public readonly record struct ZoneTexHeaderEntry(
        uint Checksum,
        ulong Tex0,
        uint DataSize,
        uint DataOffset,
        uint PaletteBytes,
        uint UploadOffset,
        uint MipLevelCount = 0,
        uint BasePixelBytes = 0,
        uint LayoutMode = 0);

    public readonly record struct ZoneTexHeaderSourceEntry(
        ZoneTexHeaderEntry Entry,
        int SourceIndex);

}
