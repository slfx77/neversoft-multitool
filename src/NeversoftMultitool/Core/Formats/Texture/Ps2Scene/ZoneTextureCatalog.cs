using System.Globalization;
using System.Net;
using System.Text;
using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

public sealed class ZoneTextureCatalog
{
    private readonly Dictionary<uint, Ps2Texture> textureCache;
    private readonly Dictionary<uint, byte[]?> pngCache = [];
    // PNG cache keyed on (checksum, normalized TEXA). For texture formats whose
    // alpha channel doesn't depend on TEXA (PSMCT32 direct, PSMT4/PSMT8 with
    // PSMCT32 CLUTs), the TEXA portion of the key is normalized to 0 so all
    // requests for that checksum share one PNG.
    private readonly Dictionary<(uint Checksum, ulong Texa), byte[]?> texaPngCache = [];
    private readonly List<SourceInfo> sources;
    private readonly List<EntryRecord> entries;
    private readonly Dictionary<(int Source, ulong Key), List<EntryRecord>> entriesBySourceIdentity;
    private readonly Dictionary<ulong, List<EntryRecord>> entriesByIdentity;
    private readonly Dictionary<(int Source, uint Tbp, uint Cbp), List<EntryRecord>> entriesBySourceTbpCbp;
    private readonly Dictionary<(uint Tbp, uint Cbp), List<EntryRecord>> entriesByTbpCbp;
    private readonly SourceInfo mainSource;

    private ZoneTextureCatalog(
        Dictionary<uint, Ps2Texture> textureCache,
        List<SourceInfo> sources,
        List<EntryRecord> entries)
    {
        this.textureCache = textureCache;
        this.sources = sources;
        this.entries = entries;
        var mainSourceIndex = sources.FindIndex(static source => source.IsMain);
        mainSource = mainSourceIndex >= 0 ? sources[mainSourceIndex] : sources.First();

        entriesBySourceIdentity = entries
            .GroupBy(static entry => (entry.Source.Index, ZoneTextureProviderBuilder.MakeTex0IdentityKey(entry.Entry.Tex0)))
            .ToDictionary(static group => group.Key, static group => group.ToList());
        entriesByIdentity = entries
            .GroupBy(static entry => ZoneTextureProviderBuilder.MakeTex0IdentityKey(entry.Entry.Tex0))
            .ToDictionary(static group => group.Key, static group => group.ToList());
        entriesBySourceTbpCbp = entries
            .GroupBy(static entry => (entry.Source.Index, GetTbp(entry.Entry.Tex0), GetCbp(entry.Entry.Tex0)))
            .ToDictionary(static group => group.Key, static group => group.ToList());
        entriesByTbpCbp = entries
            .GroupBy(static entry => (GetTbp(entry.Entry.Tex0), GetCbp(entry.Entry.Tex0)))
            .ToDictionary(static group => group.Key, static group => group.ToList());
    }

    public IReadOnlyList<string> SourceLabels => sources.Select(static source => source.Label).ToList();

    public IReadOnlyList<ZoneTextureCatalogEntry> Entries => entries
        .Select(static entry => new ZoneTextureCatalogEntry(
            entry.Entry.Checksum,
            entry.Entry.Tex0,
            entry.Source.Label,
            entry.EntryLabel))
        .ToList();

    public static bool TryBuild(
        string? texPath,
        out ZoneTextureCatalog? catalog,
        Action<string>? log = null)
    {
        catalog = null;
        if (texPath == null)
            return false;

        var texFiles = ZoneTextureProviderBuilder.GetTexFiles(texPath);
        if (texFiles.Count == 0)
            return false;

        var textureCache = new Dictionary<uint, Ps2Texture>();
        var sources = new List<SourceInfo>();
        var entries = new List<EntryRecord>();
        var zoneTexCount = 0;
        var mainPath = File.Exists(texPath) ? Path.GetFullPath(texPath) : null;

        for (var sourceIndex = 0; sourceIndex < texFiles.Count; sourceIndex++)
        {
            var tf = texFiles[sourceIndex];
            var source = new SourceInfo(
                sourceIndex,
                Path.GetFullPath(tf),
                Path.GetFileName(tf),
                mainPath != null && string.Equals(Path.GetFullPath(tf), mainPath, StringComparison.OrdinalIgnoreCase));
            sources.Add(source);

            try
            {
                var data = File.ReadAllBytes(tf);
                if (tf.EndsWith(".pak.ps2", StringComparison.OrdinalIgnoreCase)
                    && PakArchive.IsPakArchive(tf))
                {
                    foreach (var entry in PakArchive.GetTypedEntries(tf))
                    {
                        if (entry.TypeHash is not (0x2B0A3095u /* .stex */ or 0x8BFA5E8Eu /* .tex */))
                            continue;

                        var off = entry.Entry.Offset;
                        var size = entry.Entry.Size;
                        if (off < 0 || size <= 0 || off + size > data.Length)
                            continue;

                        var entryBytes = new byte[size];
                        Array.Copy(data, off, entryBytes, 0, (int)size);
                        ParseZoneTexBytes(
                            entryBytes,
                            source,
                            $"{source.Label}::{off:X8}",
                            off,
                            textureCache,
                            entries,
                            ref zoneTexCount,
                            log);
                    }

                    continue;
                }

                if (ThawZoneTexFile.IsThawZoneTex(data))
                {
                    ParseZoneTexBytes(
                        data,
                        source,
                        source.Label,
                        0,
                        textureCache,
                        entries,
                        ref zoneTexCount,
                        log);
                }
            }
            catch
            {
                // Skip unreadable files.
            }
        }

        if (zoneTexCount == 0 || textureCache.Count == 0 || entries.Count == 0)
            return false;

        log?.Invoke($"Decoded {textureCache.Count} textures from {zoneTexCount} zone TEX file(s)");
        catalog = new ZoneTextureCatalog(textureCache, sources, entries);
        return true;
    }

    internal static ZoneTextureCatalog CreateForTests(
        string mainSourceLabel,
        IEnumerable<(string SourceLabel, ThawZoneTexFile.ZoneTexHeaderEntry Entry, Ps2Texture Texture)> rows)
    {
        var sourceMap = new Dictionary<string, SourceInfo>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<SourceInfo>();
        var entries = new List<EntryRecord>();
        var textures = new Dictionary<uint, Ps2Texture>();

        foreach (var row in rows)
        {
            if (!sourceMap.TryGetValue(row.SourceLabel, out var source))
            {
                source = new SourceInfo(
                    sources.Count,
                    row.SourceLabel,
                    row.SourceLabel,
                    string.Equals(row.SourceLabel, mainSourceLabel, StringComparison.OrdinalIgnoreCase));
                sourceMap[row.SourceLabel] = source;
                sources.Add(source);
            }

            textures.TryAdd(row.Texture.Checksum, row.Texture);
            entries.Add(new EntryRecord(row.Entry, source, row.SourceLabel, EntryOffset: 0, PrimaryGroupIndex: 0));
        }

        return new ZoneTextureCatalog(textures, sources, entries);
    }

    internal static ZoneTextureCatalog CreateForTests(
        string mainSourceLabel,
        IEnumerable<(string SourceLabel, ThawZoneTexFile.ZoneTexHeaderEntry Entry, Ps2Texture Texture,
            uint PrimaryGroupIndex)> rows)
    {
        var sourceMap = new Dictionary<string, SourceInfo>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<SourceInfo>();
        var entries = new List<EntryRecord>();
        var textures = new Dictionary<uint, Ps2Texture>();

        foreach (var row in rows)
        {
            if (!sourceMap.TryGetValue(row.SourceLabel, out var source))
            {
                source = new SourceInfo(
                    sources.Count,
                    row.SourceLabel,
                    row.SourceLabel,
                    string.Equals(row.SourceLabel, mainSourceLabel, StringComparison.OrdinalIgnoreCase));
                sourceMap[row.SourceLabel] = source;
                sources.Add(source);
            }

            textures.TryAdd(row.Texture.Checksum, row.Texture);
            entries.Add(new EntryRecord(row.Entry, source, row.SourceLabel, EntryOffset: 0, row.PrimaryGroupIndex));
        }

        return new ZoneTextureCatalog(textures, sources, entries);
    }

    internal static ZoneTextureCatalog CreateForTests(
        string mainSourceLabel,
        IEnumerable<(string SourceLabel, string EntryLabel, ThawZoneTexFile.ZoneTexHeaderEntry Entry,
            Ps2Texture Texture)> rows)
    {
        var sourceMap = new Dictionary<string, SourceInfo>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<SourceInfo>();
        var entries = new List<EntryRecord>();
        var textures = new Dictionary<uint, Ps2Texture>();

        foreach (var row in rows)
        {
            if (!sourceMap.TryGetValue(row.SourceLabel, out var source))
            {
                source = new SourceInfo(
                    sources.Count,
                    row.SourceLabel,
                    row.SourceLabel,
                    string.Equals(row.SourceLabel, mainSourceLabel, StringComparison.OrdinalIgnoreCase));
                sourceMap[row.SourceLabel] = source;
                sources.Add(source);
            }

            textures.TryAdd(row.Texture.Checksum, row.Texture);
            entries.Add(new EntryRecord(row.Entry, source, row.EntryLabel, EntryOffset: 0, PrimaryGroupIndex: 0));
        }

        return new ZoneTextureCatalog(textures, sources, entries);
    }

    public MeshChecksumTextureResolver CreateTextureResolver() => checksum => GetPng(checksum);

    /// <summary>
    ///     TEXA-aware variant. PSMCT16/PSMCT24/paletted-with-PSMCT16-CLUT
    ///     textures get their alpha channel re-expanded based on the TEXA
    ///     register state from the consumer leaf. Other formats ignore TEXA
    ///     and share one cached PNG with the legacy provider.
    /// </summary>
    public Ps2TexaTextureResolver CreateTexaAwareTextureResolver() =>
        (checksum, texa) => GetPng(checksum, texa);

    public Ps2Tex0ChecksumResolver CreateTex0ChecksumResolver(string? sourceHint = null) =>
        (dmaTex0, groupChecksum) => ResolveTex0(dmaTex0, sourceHint, groupChecksum).Checksum;

    public Func<ulong, uint, Ps2GeomTextureResolution> CreateDebugTex0Resolver(string? sourceHint = null) =>
        (dmaTex0, groupChecksum) => ResolveTex0(dmaTex0, sourceHint, groupChecksum);

    public string? FindTextureEntryHintBefore(string? sourceHint, long contentOffset)
    {
        var source = FindSource(sourceHint) ?? mainSource;
        return entries
            .Where(entry => entry.Source.Index == source.Index && entry.EntryOffset <= contentOffset)
            .GroupBy(static entry => (entry.EntryLabel, entry.EntryOffset))
            .OrderByDescending(static group => group.Key.EntryOffset)
            .Select(static group => group.Key.EntryLabel)
            .FirstOrDefault();
    }

    public Ps2GeomTextureResolution ResolveTex0(ulong tex0, string? sourceHint = null, uint groupChecksum = 0)
    {
        var hintedEntries = FindEntryHintEntries(sourceHint);
        var source = hintedEntries.Count > 0
            ? hintedEntries[0].Source
            : FindSource(sourceHint) ?? mainSource;
        var identityKey = ZoneTextureProviderBuilder.MakeTex0IdentityKey(tex0);
        var tbp = GetTbp(tex0);
        var cbp = GetCbp(tex0);
        EntryRecord entry;
        string groupMode;

        if (hintedEntries.Count > 0)
        {
            var identityMatches = hintedEntries
                .Where(entryRecord => ZoneTextureProviderBuilder.MakeTex0IdentityKey(entryRecord.Entry.Tex0) == identityKey)
                .ToList();
            if (TryResolveByGroup(identityMatches, groupChecksum, out entry, out groupMode))
                return MakeResolution(entry, $"entry_{groupMode}_exact");
            if (TryResolveUnique(identityMatches, out entry))
                return MakeResolution(entry, "entry_exact");

            var tbpCbpMatches = hintedEntries
                .Where(entryRecord => GetTbp(entryRecord.Entry.Tex0) == tbp && GetCbp(entryRecord.Entry.Tex0) == cbp)
                .ToList();
            if (TryResolveByGroup(tbpCbpMatches, groupChecksum, out entry, out groupMode))
                return MakeResolution(entry, $"entry_{groupMode}_tbp_cbp");
            if (TryResolveUnique(tbpCbpMatches, out entry))
                return MakeResolution(entry, "entry_tbp_cbp");
        }

        if (TryResolveByGroup(entriesBySourceIdentity, (source.Index, identityKey), groupChecksum,
                out entry, out groupMode))
            return MakeResolution(entry, $"same_source_{groupMode}_exact");
        if (TryResolveUnique(entriesBySourceIdentity, (source.Index, identityKey), out entry))
            return MakeResolution(entry, "same_source_exact");
        if (TryResolveUnique(entriesByIdentity, identityKey, out entry))
            return MakeResolution(entry, "unique_exact");
        if (TryResolveByGroup(entriesBySourceTbpCbp, (source.Index, tbp, cbp), groupChecksum,
                out entry, out groupMode))
            return MakeResolution(entry, $"same_source_{groupMode}_tbp_cbp");
        if (TryResolveUnique(entriesBySourceTbpCbp, (source.Index, tbp, cbp), out entry))
            return MakeResolution(entry, "same_source_tbp_cbp");

        if (source.Index != mainSource.Index)
        {
            if (TryResolveByGroup(entriesBySourceIdentity, (mainSource.Index, identityKey), groupChecksum,
                    out entry, out groupMode))
                return MakeResolution(entry, $"main_source_{groupMode}_exact");
            if (TryResolveUnique(entriesBySourceIdentity, (mainSource.Index, identityKey), out entry))
                return MakeResolution(entry, "main_source_exact");
            if (TryResolveByGroup(entriesBySourceTbpCbp, (mainSource.Index, tbp, cbp), groupChecksum,
                    out entry, out groupMode))
                return MakeResolution(entry, $"main_source_{groupMode}_tbp_cbp");
            if (TryResolveUnique(entriesBySourceTbpCbp, (mainSource.Index, tbp, cbp), out entry))
                return MakeResolution(entry, "main_source_tbp_cbp");
        }

        if (TryResolveUnique(entriesByTbpCbp, (tbp, cbp), out entry))
            return MakeResolution(entry, "unique_tbp_cbp");

        return new Ps2GeomTextureResolution(0, "unresolved", "", "");
    }

    public void WriteDebugDump(string debugDir)
    {
        Directory.CreateDirectory(debugDir);
        var textureDir = Path.Combine(debugDir, "textures");
        Directory.CreateDirectory(textureDir);

        foreach (var texture in textureCache.Values.OrderBy(static texture => texture.Checksum))
        {
            var png = GetPng(texture.Checksum);
            if (png == null)
                continue;

            File.WriteAllBytes(Path.Combine(textureDir, $"{texture.Checksum:X8}.png"), png);
        }

        WriteTextureCatalogCsv(Path.Combine(debugDir, "texture_catalog.csv"));
        WriteTextureIndexHtml(Path.Combine(debugDir, "texture_index.html"));
    }

    private void WriteTextureCatalogCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("checksum,source,entry,group_checksum,primary_group_index,tex0,tbp,tbw,psm,tw,th,cbp,cpsm,csm,csa,width,height");
        foreach (var entry in entries.OrderBy(static entry => entry.Source.Label)
                     .ThenBy(static entry => entry.Entry.Checksum)
                     .ThenBy(static entry => entry.EntryLabel))
        {
            textureCache.TryGetValue(entry.Entry.Checksum, out var texture);
            var tex0 = entry.Entry.Tex0;
            sb.Append(CsvHex(entry.Entry.Checksum)).Append(',')
                .Append(Csv(entry.Source.Label)).Append(',')
                .Append(Csv(entry.EntryLabel)).Append(',')
                .Append(CsvHex(entry.Entry.GroupChecksum)).Append(',')
                .Append(entry.PrimaryGroupIndex.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvHex(tex0)).Append(',')
                .Append(GetTbp(tex0).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(((tex0 >> 14) & 0x3F).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(((tex0 >> 20) & 0x3F).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((1 << (int)((tex0 >> 26) & 0xF)).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((1 << (int)((tex0 >> 30) & 0xF)).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(GetCbp(tex0).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(((tex0 >> 51) & 0xF).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(((tex0 >> 55) & 0x1).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(((tex0 >> 56) & 0x1F).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((texture?.Width ?? 0).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((texture?.Height ?? 0).ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    private void WriteTextureIndexHtml(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><meta charset=\"utf-8\"><title>Worldzone Textures</title>");
        sb.AppendLine("<style>body{font-family:sans-serif} .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:12px}.tex{border:1px solid #ccc;padding:8px}.tex img{image-rendering:pixelated;max-width:100%;background:#888}</style>");
        sb.AppendLine("<h1>Worldzone Textures</h1><div class=\"grid\">");
        foreach (var texture in textureCache.Values.OrderBy(static texture => texture.Checksum))
        {
            var checksum = $"{texture.Checksum:X8}";
            var name = WebUtility.HtmlEncode(texture.Name ?? checksum);
            sb.Append("<div class=\"tex\"><a href=\"textures/")
                .Append(checksum)
                .Append(".png\"><img src=\"textures/")
                .Append(checksum)
                .Append(".png\" loading=\"lazy\"></a><div><code>")
                .Append(checksum)
                .Append("</code></div><div>")
                .Append(name)
                .Append("</div><div>")
                .Append(texture.Width)
                .Append("x")
                .Append(texture.Height)
                .AppendLine("</div></div>");
        }

        sb.AppendLine("</div>");
        File.WriteAllText(path, sb.ToString());
    }

    private byte[]? GetPng(uint checksum)
    {
        if (pngCache.TryGetValue(checksum, out var cached))
            return cached;

        if (!textureCache.TryGetValue(checksum, out var texture) || texture.Pixels == null)
        {
            pngCache[checksum] = null;
            return null;
        }

        var png = ImageWriter.WritePngToMemory(texture.Width, texture.Height, texture.Pixels);
        pngCache[checksum] = png;
        return png;
    }

    // Deterministic TEXA used when a leaf's DMA chain carries no inline
    // TEXA register write (the universal case for THAW worldzones, which
    // load TEXA from EE preset code rather than the GIF stream). PCSX2 GS
    // traces show the engine writes only four distinct TEXA values across
    // the binary, and renders every TEXA-relevant texture binding with
    // both TA0=0 (colour pass) and TA0=128 (depth/clear pass) within the
    // same frame. For a single-pass static export the colour-pass value
    // is the right one: it preserves cutout transparency on textures with
    // any MSB=0 CLUT entries, and resolves to alpha=128 (visually
    // identical to TA0=128) for textures whose CLUT MSBs are all 1.
    private const ulong UnsetTexaDeterministicDefault = 0x0000008000000000UL;

    /// <summary>
    ///     TEXA-aware variant: re-applies alpha expansion for non-32-bit
    ///     pixel formats based on the supplied TEXA. Caches per
    ///     (checksum, normalizedTexa) so each unique TEXA combination is
    ///     decoded once. For formats unaffected by TEXA the normalization
    ///     collapses to (checksum, 0) and the cache shares with the
    ///     non-TEXA-aware provider.
    /// </summary>
    private byte[]? GetPng(uint checksum, ulong texa)
    {
        if (!textureCache.TryGetValue(checksum, out var texture) || texture.Pixels == null)
            return null;

        var formatRespectsTexa = TextureFormatRespectsTexa(texture.Psm, texture.Cpsm);

        // Substitute the deterministic colour-pass TEXA (TA0=0 TA1=128 AEM=0)
        // when the leaf carried no inline TEXA write and the format actually
        // consumes TEXA. See UnsetTexaDeterministicDefault remark above.
        if (formatRespectsTexa && texa == 0)
            texa = UnsetTexaDeterministicDefault;

        var normalizedTexa = formatRespectsTexa ? texa : 0UL;
        var key = (checksum, normalizedTexa);
        if (texaPngCache.TryGetValue(key, out var cached))
            return cached;

        // The legacy decoder bakes the (TA0=0 TA1=128 AEM=0) rule into its
        // RGBA output (alpha=0 on pixel-value-0, 0xFF otherwise), so both
        // the deterministic-default and TEXA-irrelevant cases share its
        // cached bytes verbatim.
        if (normalizedTexa is 0 or UnsetTexaDeterministicDefault)
        {
            var defaultPng = GetPng(checksum);
            texaPngCache[key] = defaultPng;
            return defaultPng;
        }

        // Non-default TEXA: re-apply alpha expansion to a copy of the
        // already-decoded RGBA buffer, treating (alpha==0) as "bit15 was 0"
        // and (alpha!=0) as "bit15 was 1" then overwriting with TA0/TA1
        // (PS2 0..128 -> PNG 0..255 doubled).
        var ta0 = (byte)Math.Min(((texa & 0xFF) * 2), 255);
        var ta1 = (byte)Math.Min((((texa >> 32) & 0xFF) * 2), 255);
        var aem = ((texa >> 15) & 1) != 0;
        var rgba = (byte[])texture.Pixels.Clone();
        ApplyTexaAlpha(rgba, texture.Psm, texture.Cpsm, ta0, ta1, aem);
        var png = ImageWriter.WritePngToMemory(texture.Width, texture.Height, rgba);
        texaPngCache[key] = png;
        return png;
    }

    /// <summary>
    ///     Returns true when the given (PSM, CPSM) combination's alpha
    ///     channel is influenced by TEXA. PSMCT16 direct textures, PSMCT24
    ///     direct textures, and paletted formats sampling a PSMCT16 CLUT
    ///     all read TEXA at draw time. PSMCT32 direct and paletted-with-
    ///     PSMCT32-CLUT carry full per-texel/CLUT-entry alpha and ignore
    ///     TEXA entirely.
    /// </summary>
    private static bool TextureFormatRespectsTexa(uint psm, uint cpsm)
    {
        // PSMCT16 (0x02) / PSMCT16S (0x0A) / PSMCT24 (0x01) direct textures
        if (psm is 0x01 or 0x02 or 0x0A) return true;
        // PSMT4 (0x14) / PSMT8 (0x13) sampling a 16-bit CLUT
        if (psm is 0x13 or 0x14 && cpsm is 0x02 or 0x0A) return true;
        return false;
    }

    /// <summary>
    ///     Re-applies TEXA-driven alpha expansion to an already-decoded RGBA
    ///     buffer. Heuristic: the legacy decoder mapped pixel-value-0 to
    ///     alpha=0 and any non-zero pixel to alpha=0xFF (effectively
    ///     TA0=0, TA1=128, AEM=0). To redo with the supplied TEXA we treat
    ///     (alpha==0) as "bit15 was 0" and (alpha!=0) as "bit15 was 1",
    ///     overwriting with TA0/TA1 (PS2 0..128 -> PNG 0..255 doubled).
    /// </summary>
    private static void ApplyTexaAlpha(byte[] rgba, uint psm, uint cpsm, byte ta0, byte ta1, bool aem)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var bit15WasZero = rgba[i + 3] == 0;
            byte alpha;
            if (bit15WasZero)
                alpha = ta0;
            else
                alpha = ta1;
            // AEM (16-bit only): force alpha=0 when RGB are all zero.
            if (aem && rgba[i] == 0 && rgba[i + 1] == 0 && rgba[i + 2] == 0)
                alpha = 0;
            rgba[i + 3] = alpha;
        }
        _ = psm;
        _ = cpsm;
    }

    private SourceInfo? FindSource(string? sourceHint)
    {
        if (string.IsNullOrWhiteSpace(sourceHint))
            return null;

        var fullPath = File.Exists(sourceHint) ? Path.GetFullPath(sourceHint) : sourceHint;
        return sources.FirstOrDefault(source =>
            string.Equals(source.Path, fullPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.Label, sourceHint, StringComparison.OrdinalIgnoreCase));
    }

    private static void ParseZoneTexBytes(
        byte[] data,
        SourceInfo source,
        string label,
        long entryOffset,
        Dictionary<uint, Ps2Texture> textureCache,
        List<EntryRecord> entries,
        ref int zoneTexCount,
        Action<string>? log)
    {
        if (!ThawZoneTexFile.IsThawZoneTex(data))
            return;

        zoneTexCount++;
        var textures = ThawZoneTexFile.DecodeAllFromFile(data);
        var headerEntries = ThawZoneTexFile.ParseHeaderEntries(data);
        var primaryGroupIndices = ParsePrimaryGroupIndices(data);

        foreach (var texture in textures)
            textureCache.TryAdd(texture.Checksum, texture);

        foreach (var entry in headerEntries)
        {
            primaryGroupIndices.TryGetValue(entry.GroupChecksum, out var primaryGroupIndex);
            entries.Add(new EntryRecord(entry, source, label, entryOffset, primaryGroupIndex));
        }

        log?.Invoke($"Detected zone TEX: {label} ({headerEntries.Count} records, {textures.Count} textures)");
    }

    private static Dictionary<uint, uint> ParsePrimaryGroupIndices(ReadOnlySpan<byte> data)
    {
        var map = new Dictionary<uint, uint>();
        if (!ThawZoneTexOwnerBlobDecoder.TryFindOwnerBlobHeader(
                data,
                out var headerOffset,
                out var primaryCount,
                out _,
                out _,
                out _,
                out _))
        {
            return map;
        }

        var primaryStart = headerOffset + 0x10;
        for (var i = 0; i < primaryCount; i++)
        {
            var groupOffset = primaryStart + i * 0x50 + 0x08;
            if (groupOffset + sizeof(uint) > data.Length)
                break;

            var groupChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data[groupOffset..]);
            if (groupChecksum != 0)
                map.TryAdd(groupChecksum, (uint)i + 1);
        }

        return map;
    }

    private static bool TryResolveByGroup<TKey>(
        IReadOnlyDictionary<TKey, List<EntryRecord>> map,
        TKey key,
        uint groupChecksum,
        out EntryRecord entry,
        out string groupMode)
        where TKey : notnull
    {
        entry = default;
        groupMode = "";
        if (groupChecksum == 0 || !map.TryGetValue(key, out var candidates) || candidates.Count == 0)
            return false;

        var groupMatches = candidates
            .Where(candidate => MatchesGroup(candidate, groupChecksum))
            .ToList();
        if (groupMatches.Count == 0)
            return false;

        var distinct = groupMatches
            .GroupBy(static candidate => candidate.Entry.Checksum)
            .ToList();
        if (distinct.Count != 1)
            return false;

        entry = OrderCandidates(distinct[0]).First();
        groupMode = entry.Entry.GroupChecksum == groupChecksum ? "group" : "material_group";
        return true;
    }

    private static bool TryResolveByGroup(
        IReadOnlyList<EntryRecord> candidates,
        uint groupChecksum,
        out EntryRecord entry,
        out string groupMode)
    {
        entry = default;
        groupMode = "";
        if (groupChecksum == 0 || candidates.Count == 0)
            return false;

        var groupMatches = candidates
            .Where(candidate => MatchesGroup(candidate, groupChecksum))
            .ToList();
        if (groupMatches.Count == 0)
            return false;

        var distinct = groupMatches
            .GroupBy(static candidate => candidate.Entry.Checksum)
            .ToList();
        if (distinct.Count != 1)
            return false;

        entry = OrderCandidates(distinct[0]).First();
        groupMode = entry.Entry.GroupChecksum == groupChecksum ? "group" : "material_group";
        return true;
    }

    private static bool TryResolveUnique<TKey>(
        IReadOnlyDictionary<TKey, List<EntryRecord>> map,
        TKey key,
        out EntryRecord entry)
        where TKey : notnull
    {
        entry = default;
        if (!map.TryGetValue(key, out var candidates) || candidates.Count == 0)
            return false;

        var distinct = candidates
            .GroupBy(static candidate => candidate.Entry.Checksum)
            .ToList();
        if (distinct.Count != 1)
            return false;

        entry = OrderCandidates(distinct[0]).First();
        return true;
    }

    private static bool TryResolveUnique(
        IReadOnlyList<EntryRecord> candidates,
        out EntryRecord entry)
    {
        entry = default;
        if (candidates.Count == 0)
            return false;

        var distinct = candidates
            .GroupBy(static candidate => candidate.Entry.Checksum)
            .ToList();
        if (distinct.Count != 1)
            return false;

        entry = OrderCandidates(distinct[0]).First();
        return true;
    }

    private List<EntryRecord> FindEntryHintEntries(string? sourceHint)
    {
        if (string.IsNullOrWhiteSpace(sourceHint))
            return [];

        if (!sourceHint.Contains("::", StringComparison.Ordinal))
            return [];

        return entries
            .Where(entry => string.Equals(entry.EntryLabel, sourceHint, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static IEnumerable<EntryRecord> OrderCandidates(IEnumerable<EntryRecord> candidates) =>
        candidates
            .OrderBy(static candidate => candidate.Source.Index)
            .ThenBy(static candidate => candidate.EntryLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.PrimaryGroupIndex)
            .ThenBy(static candidate => candidate.Entry.UploadOffset)
            .ThenBy(static candidate => candidate.Entry.DataOffset);

    private static bool MatchesGroup(EntryRecord entry, uint groupChecksum) =>
        entry.Entry.GroupChecksum == groupChecksum
        || (entry.PrimaryGroupIndex != 0 && entry.PrimaryGroupIndex == groupChecksum);

    private static Ps2GeomTextureResolution MakeResolution(EntryRecord entry, string mode) =>
        new(entry.Entry.Checksum, mode, entry.Source.Label, entry.EntryLabel);

    private static uint GetTbp(ulong tex0) => (uint)(tex0 & 0x3FFF);

    private static uint GetCbp(ulong tex0) => (uint)((tex0 >> 37) & 0x3FFF);

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string CsvHex(uint value) => $"0x{value:X8}";

    private static string CsvHex(ulong value) => $"0x{value:X16}";

    private readonly record struct SourceInfo(int Index, string Path, string Label, bool IsMain);

    private readonly record struct EntryRecord(
        ThawZoneTexFile.ZoneTexHeaderEntry Entry,
        SourceInfo Source,
        string EntryLabel,
        long EntryOffset,
        uint PrimaryGroupIndex);
}

public readonly record struct ZoneTextureCatalogEntry(
    uint Checksum,
    ulong Tex0,
    string SourceLabel,
    string EntryLabel);
