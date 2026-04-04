using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

/// <summary>
///     Parser for THAW PS2 world-zone .tex files extracted from PAK archives.
///     These files contain a record table plus CPU-side source slots plus a DMA upload stream.
///     Public decode paths prefer the prepared source-slot path seen in the decompiled runtime
///     and fall back to upload-snapshot GS VRAM decode when source-slot decode cannot resolve
///     an entry.
///     Format documented in tools/ghidra/thaw-ps2/output/zone_tex_format.md.
/// </summary>
public static class ThawZoneTexFile
{
    // ── Analyzer-compat: CLUT reorder tables ────────────────────────────

    internal static readonly int[] ClutTableT16I4 =
    [
        0, 2, 8, 10, 16, 18, 24, 26,
        4, 6, 12, 14, 20, 22, 28, 30
    ];

    internal static readonly int[] ClutTableT32I4 =
    [
        0, 1, 4, 5, 8, 9, 12, 13,
        2, 3, 6, 7, 10, 11, 14, 15
    ];

    internal static readonly int[] Layout02000001BlockPermutation = [0, 3, 1, 2, 4];
    internal static readonly int[] Layout02000005BlockPermutation = [0, 2, 1, 3, 4];
    internal static readonly int[] Layout02000005TilePermutation = [1, 5, 0, 4, 2, 3];

    /// <summary>
    ///     Detect world-zone TEX files by discovering the record table.
    /// </summary>
    public static bool IsThawZoneTex(ReadOnlySpan<byte> data)
    {
        return ThawZoneTexCoreDecoder.IsZoneTex(data);
    }

    /// <summary>
    ///     Parse the zone .tex file and return the upload list (for backward compatibility).
    /// </summary>
    public static ZoneTexResult Parse(ReadOnlySpan<byte> data)
    {
        var uploads = ParseVramUploads(data);
        return new ZoneTexResult(uploads);
    }

    /// <summary>
    ///     Parse the fixed-size texture metadata records from the record table.
    ///     Uses the DMA-chain-based discovery algorithm to find and parse all records.
    /// </summary>
    public static List<ZoneTexHeaderEntry> ParseHeaderEntries(ReadOnlySpan<byte> data)
    {
        return ThawZoneTexCoreDecoder.DiscoverAndParseRecords(data);
    }

    /// <summary>
    ///     Parse all GIF A+D upload blocks from the DMA chain.
    ///     Kept for backward compatibility with analyzer tools and VRAM-based decode paths.
    /// </summary>
    internal static List<VramUpload> ParseVramUploads(ReadOnlySpan<byte> data)
    {
        return ThawZoneTexVramSupport.ParseVramUploads(data);
    }

    /// <summary>
    ///     Decode all textures from a zone .tex file by preferring the CPU-side prepared source
    ///     slots described by the record table, with upload-snapshot VRAM decode as fallback.
    /// </summary>
    public static List<Ps2Texture> DecodeAllFromFile(ReadOnlySpan<byte> data,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        return ThawZoneTexCoreDecoder.DecodeAllRecords(data, gifQwordWordOrder);
    }

    /// <summary>
    ///     Decode specific header entries from a zone .tex file by preferring the file-backed
    ///     prepared source slots. If uploads are not supplied, they are derived from the file's
    ///     DMA chain and used as fallback snapshots.
    /// </summary>
    public static List<Ps2Texture> DecodeFromHeaderEntries(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        IEnumerable<ZoneTexHeaderEntry> headerEntries,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var entryList = headerEntries as IReadOnlyList<ZoneTexHeaderEntry> ?? headerEntries.ToList();
        if (entryList.Count == 0)
            return [];

        var effectiveUploads = uploads.Count > 0 ? uploads : ParseVramUploads(fileData);
        return ThawZoneTexCoreDecoder.DecodeEntriesFromPreparedSourcesOrUploadSnapshots(
            fileData, effectiveUploads, entryList, gifQwordWordOrder);
    }

    /// <summary>
    ///     Decode textures from header entries using upload snapshots (no file data).
    ///     Kept for backward compatibility with analyzer tools.
    /// </summary>
    public static List<Ps2Texture> DecodeFromHeaderEntries(
        IReadOnlyList<VramUpload> uploads, IEnumerable<ZoneTexHeaderEntry> headerEntries,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        var entryList = headerEntries as IReadOnlyList<ZoneTexHeaderEntry> ?? headerEntries.ToList();
        return ThawZoneTexCoreDecoder.DecodeEntriesFromUploadSnapshots(uploads, entryList, gifQwordWordOrder);
    }

    /// <summary>
    ///     Decode textures by matching TEX0 values to header entries via TBP/CBP.
    /// </summary>
    public static List<Ps2Texture> DecodeFromTex0Values(
        IReadOnlyList<VramUpload> uploads, IEnumerable<ulong> tex0Values,
        IReadOnlyDictionary<(uint Tbp, uint Cbp), uint>? checksumMap = null,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        // Fall back to VRAM-based decode since we don't have the file data here
        var vram = ThawZoneTexVramSupport.BuildVram(uploads, gifQwordWordOrder);
        var textures = new List<Ps2Texture>();

        foreach (var tex0 in tex0Values)
        {
            var tbp = (uint)(tex0 & 0x3FFF);
            var cbp = (uint)((tex0 >> 37) & 0x3FFF);
            var checksum = checksumMap != null && checksumMap.TryGetValue((tbp, cbp), out var ck)
                ? ck
                : (tbp << 16) | cbp;
            if (checksum == 0)
                continue;

            if (textures.Any(t => t.Checksum == checksum))
                continue;

            var decoded = ThawZoneTexVramSupport.DecodeFromTex0(vram, tex0);
            if (decoded == null)
                continue;

            var psm = (uint)((tex0 >> 20) & 0x3F);
            var cpsm = (uint)((tex0 >> 51) & 0xF);
            var tw = 1 << (int)((tex0 >> 26) & 0xF);
            var th = 1 << (int)((tex0 >> 30) & 0xF);
            textures.Add(new Ps2Texture(checksum, tw, th, psm, cpsm, decoded));
        }

        return textures;
    }

    /// <summary>
    ///     Build a texture provider and TEX0 mapping for integration with the existing pipeline.
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

        var textures = DecodeFromTex0Values(uploads, knownTex0Values, checksumMap, gifQwordWordOrder);
        foreach (var tex in textures)
            textureCache.TryAdd(tex.Checksum, tex);

        foreach (var tex0 in knownTex0Values)
        {
            var tbp = (uint)(tex0 & 0x3FFF);
            var cbp = (uint)((tex0 >> 37) & 0x3FFF);
            var checksum = checksumMap != null && checksumMap.TryGetValue((tbp, cbp), out var ck)
                ? ck
                : (tbp << 16) | cbp;
            if (checksum != 0)
                tex0Map.TryAdd((0, tbp, cbp), checksum);
        }

        return (textureCache, tex0Map);
    }

    // ── MDL support ─────────────────────────────────────────────────────

    public static List<MdlGsTextureState> ExtractTextureStatesFromMdl(byte[] mdlData)
    {
        return ThawZoneTexMdlSupport.ExtractTextureStatesFromMdl(mdlData);
    }

    public static HashSet<ulong> ExtractTex0ValuesFromMdl(byte[] mdlData)
    {
        return ThawZoneTexMdlSupport.ExtractTex0ValuesFromMdl(mdlData);
    }

    // ── Header source resolution (used by Ps2TexCommand, Ps2TextureLoader) ──

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

    // ── Backward-compatible internal methods (used by ThawZoneTexAnalyzer) ──

    internal static int FindFirstGifAdBlock(ReadOnlySpan<byte> data)
    {
        return ThawZoneTexVramSupport.FindFirstGifAdBlock(data);
    }

    internal static Ps2GsVram BuildVram(IEnumerable<VramUpload> uploads,
        Ps2GifQwordWordOrder? gifQwordWordOrder = null)
    {
        return ThawZoneTexVramSupport.BuildVram(uploads, gifQwordWordOrder);
    }

    internal static byte[]? DecodeFromTex0(Ps2GsVram vram, ulong tex0)
    {
        return ThawZoneTexVramSupport.DecodeFromTex0(vram, tex0);
    }

    internal static byte[]? ReadClutPsmt4Csm1(Ps2GsVram vram, uint cbp, uint cpsm)
    {
        return ThawZoneTexVramSupport.ReadClutPsmt4Csm1(vram, cbp, cpsm);
    }

    // ── Analyzer-compat: stub methods for heuristic-based decode paths ──
    // These methods are retained so the ThawZoneTexAnalyzer project compiles.
    // They delegate to the old support files which are kept for this purpose.

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

        var decodedChecksums = new HashSet<uint>();
        foreach (var entry in headerEntries)
        {
            if (!decodedChecksums.Add(entry.Checksum))
                continue;

            var texture = ThawZoneTexHeaderDataSupport.DecodeBestHeaderDataSlot(
                fileData, uploads, dataBaseOffset, dataOffsetBias, entry);
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
        return ThawZoneTexHeaderClutSupport.ShouldPreferSameCbpEntropyClut(entry);
    }

    internal static double ComputeHeaderClutEntropy(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        ZoneTexHeaderEntry entry,
        int selectedBias)
    {
        return ThawZoneTexHeaderClutSupport.ComputeHeaderClutEntropy(fileData, dataBaseOffset, entry, selectedBias);
    }

    internal static byte[] ReorderClutPsmt4ForLayout(byte[] clut, uint cpsm, uint layoutMode)
    {
        return ThawZoneTexHeaderClutSupport.ReorderClutPsmt4ForLayout(clut, cpsm, layoutMode);
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
        return ThawZoneTexHeaderLayoutSupport.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry, selectedBias,
            matchedUpload);
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

    // ── Public record types ─────────────────────────────────────────────

    public readonly record struct VramUpload(
        uint Dbp,
        uint Dbw,
        uint Dpsm,
        int Width,
        int Height,
        byte[] PixelData,
        uint RelativeDataOffset = 0,
        uint SourceDataOffset = 0);

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
        uint LayoutMode = 0,
        uint GroupChecksum = 0,
        uint CumulativeOffset = 0);

    public readonly record struct ZoneTexHeaderSourceEntry(
        ZoneTexHeaderEntry Entry,
        int SourceIndex);
}
