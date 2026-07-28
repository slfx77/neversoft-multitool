using System.Globalization;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     Accumulates replay texture dumps (fed from the interpreter's
///     TextureDumpSink) and compares each against the zone-texture catalog's
///     decode of the same checksum. Only <c>vram</c> and <c>image_upload</c>
///     sources participate — <c>external</c> dumps ARE the catalog decode fed
///     back through the replay (comparing them would be circular), and
///     rt_cache/framebuffer sources are render output, not assets. The
///     interpreter only stamps SourceChecksum on external resolutions, so the
///     checksum for a vram/image_upload dump is resolved here from its TEX0.
/// </summary>
internal sealed class GsTextureOracleComparer(
    MeshChecksumTextureResolver catalogResolver,
    Func<ulong, uint> resolveChecksum,
    IReadOnlyList<ZoneTextureCatalogEntry> catalogEntries)
{
    internal const string ClassificationMatch = "Match";
    internal const string ClassificationQuantizationOnly = "QuantizationOnly";
    internal const string ClassificationAlphaProtocolDiff = "AlphaProtocolDiff";
    internal const string ClassificationDivergent = "Divergent";
    internal const string ClassificationNotComparable = "NotComparable";
    internal const string ClassificationSlotReuseSuspect = "SlotReuseSuspect";

    /// <summary>Content matches a DIFFERENT catalog asset: the VRAM slot held
    ///     another zone texture than the TBP-attributed one — an attribution
    ///     error, not a decode defect.</summary>
    internal const string ClassificationAttributionMismatch = "AttributionMismatch";

    /// <summary>Content matches nothing in the zone catalog: a streamed
    ///     character/HUD/other-source asset passing through the slot — outside
    ///     the zone-TEX decode question entirely.</summary>
    internal const string ClassificationForeignContent = "ForeignContent";

    private const double MatchMae = 2.0;

    /// <summary>
    ///     PSMCT16-family storage quantizes to 5 bits/channel (step 8); allow
    ///     one step of divergence before calling a difference real.
    /// </summary>
    private const double QuantizationMae = 8.0;

    private const double AlphaProtocolMae = 16.0;

    private readonly Dictionary<string, GsTextureOracleRow> rows = [];
    private readonly Dictionary<uint, (byte[] Rgba, int Width, int Height)?> catalogCache = [];

    public void Add(GsRuntimeTextureDump dump)
    {
        var audit = dump.Audit;
        if (audit.Source is not ("vram" or "image_upload"))
            return;

        var checksum = audit.SourceChecksum ?? resolveChecksum(ParseHex(audit.Tex0));
        if (checksum == 0)
            return;

        // One row per unique dump key; repeated resolutions of the same state
        // would only duplicate the identical comparison.
        var key = audit.Key;
        if (rows.ContainsKey(key))
            return;

        rows[key] = Compare(checksum, audit, dump.Rgba);
    }

    public GsTextureOracleReport BuildReport(string capture)
    {
        // The game streams different assets through one VRAM slot mid-frame
        // (observed: a sky gradient and a deck graphic at the same TEX0), so a
        // static TEX0→checksum attribution is only trustworthy for the
        // best-agreeing dump at each TEX0. Every other dump in a multi-content
        // group is a slot-reuse suspect, not a decode-defect lead.
        var reclassified = rows.Values
            .GroupBy(static row => row.Tex0, StringComparer.Ordinal)
            .SelectMany(static group =>
            {
                var members = group.ToList();
                if (members.Count <= 1)
                    return members;

                var best = members
                    .Where(static row => row.RgbMae >= 0)
                    .OrderBy(static row => row.RgbMae)
                    .FirstOrDefault();
                return members.Select(row =>
                    row.Classification == ClassificationDivergent && !ReferenceEquals(row, best)
                        ? new GsTextureOracleRow
                        {
                            Checksum = row.Checksum,
                            Tex0 = row.Tex0,
                            Texa = row.Texa,
                            Source = row.Source,
                            Classification = ClassificationSlotReuseSuspect,
                            RgbMae = row.RgbMae,
                            AlphaMae = row.AlphaMae,
                            BestMatchChecksum = row.BestMatchChecksum,
                            BestMatchRgbMae = row.BestMatchRgbMae,
                            Width = row.Width,
                            Height = row.Height,
                            RegionX = row.RegionX,
                            RegionY = row.RegionY,
                            Psm = row.Psm,
                            Cpsm = row.Cpsm,
                            Csa = row.Csa,
                            Notes = "multiple uploads observed at this TEX0; attribution uncertain"
                        }
                        : row);
            });

        var ordered = reclassified
            .OrderBy(static row => row.Checksum)
            .ThenBy(static row => row.Tex0, StringComparer.Ordinal)
            .ThenBy(static row => row.RegionX)
            .ThenBy(static row => row.RegionY)
            .ToList();
        return new GsTextureOracleReport
        {
            Capture = capture,
            Compared = ordered.Count,
            Matches = ordered.Count(static row => row.Classification == ClassificationMatch),
            QuantizationOnly = ordered.Count(static row => row.Classification == ClassificationQuantizationOnly),
            AlphaProtocolDiffs = ordered.Count(static row => row.Classification == ClassificationAlphaProtocolDiff),
            Divergent = ordered.Count(static row => row.Classification == ClassificationDivergent),
            SlotReuseSuspects = ordered.Count(static row => row.Classification == ClassificationSlotReuseSuspect),
            AttributionMismatches = ordered.Count(static row => row.Classification == ClassificationAttributionMismatch),
            ForeignContent = ordered.Count(static row => row.Classification == ClassificationForeignContent),
            NotComparable = ordered.Count(static row => row.Classification == ClassificationNotComparable),
            Rows = ordered
        };
    }

    private GsTextureOracleRow Compare(uint checksum, GsTextureDumpAuditRow audit, byte[] dumpRgba)
    {
        var catalog = ResolveCatalogPixels(checksum);
        string classification;
        double rgbMae = -1;
        double alphaMae = -1;
        uint? bestMatchChecksum = null;
        double bestMatchRgbMae = -1;
        string? notes = null;

        if (catalog == null)
        {
            classification = ClassificationNotComparable;
            notes = "catalog decode unavailable";
        }
        else if (audit.TextureWidth != catalog.Value.Width || audit.TextureHeight != catalog.Value.Height)
        {
            // A TEX0 whose decode dimensions differ from the catalog texture is
            // a mip level (or a partial re-registration) — comparing it against
            // a crop of the full-size decode would flag false divergence.
            classification = ClassificationNotComparable;
            notes = $"dump texture {audit.TextureWidth}x{audit.TextureHeight} vs catalog " +
                    $"{catalog.Value.Width}x{catalog.Value.Height} (mip level?)";
        }
        else if (!TryExtractRegion(catalog.Value, audit, out var expected))
        {
            classification = ClassificationNotComparable;
            notes = $"catalog {catalog.Value.Width}x{catalog.Value.Height} cannot supply region " +
                    $"{audit.RegionX},{audit.RegionY} {audit.Width}x{audit.Height}";
        }
        else
        {
            // The replay decodes alpha in the raw GS domain where 128 means
            // opaque, while the catalog PNG carries export-scaled alpha where
            // 255 means opaque (the two-domain rule). Normalize the dump into
            // the PNG domain before diffing so the protocol difference does
            // not read as divergence.
            var normalized = ScaleAlphaToPngDomain(dumpRgba);
            (rgbMae, alphaMae) = ComputeMae(expected, normalized);
            classification = Classify(rgbMae, alphaMae);

            // A Divergent verdict against the TBP-attributed asset is only a
            // decode lead if the content isn't simply a DIFFERENT asset parked
            // in the slot. Content-sweep every same-size catalog texture: a
            // clean hit elsewhere reclassifies as attribution error; no hit at
            // all means the slot held out-of-catalog (streamed) content.
            if (classification == ClassificationDivergent)
            {
                (bestMatchChecksum, bestMatchRgbMae) =
                    FindBestContentMatch(audit, normalized, checksum, rgbMae);
                if (bestMatchChecksum != checksum)
                {
                    classification = bestMatchChecksum != null && bestMatchRgbMae <= QuantizationMae
                        ? ClassificationAttributionMismatch
                        : ClassificationForeignContent;
                }
            }

            WriteDebugPair(checksum, audit, classification, expected, normalized);
        }

        return new GsTextureOracleRow
        {
            Checksum = checksum,
            Tex0 = audit.Tex0,
            Texa = audit.Texa,
            Source = audit.Source,
            Classification = classification,
            RgbMae = Math.Round(rgbMae, 3),
            AlphaMae = Math.Round(alphaMae, 3),
            BestMatchChecksum = bestMatchChecksum,
            BestMatchRgbMae = Math.Round(bestMatchRgbMae, 3),
            Width = audit.Width,
            Height = audit.Height,
            RegionX = audit.RegionX,
            RegionY = audit.RegionY,
            Psm = audit.Psm,
            Cpsm = audit.Cpsm,
            Csa = audit.Csa,
            Notes = notes
        };
    }

    /// <summary>
    ///     RGB-content-matches the dump against every same-dimension catalog
    ///     texture (decodes cached). Returns the best (checksum, rgbMae) — the
    ///     attributed checksum wins ties so a genuine decode lead is never
    ///     reclassified away by an equally-bad alternative.
    /// </summary>
    private (uint? Checksum, double RgbMae) FindBestContentMatch(
        GsTextureDumpAuditRow audit,
        byte[] normalizedDump,
        uint attributedChecksum,
        double attributedRgbMae)
    {
        uint? best = attributedChecksum;
        var bestMae = attributedRgbMae;
        var seen = new HashSet<uint> { attributedChecksum };
        foreach (var entry in catalogEntries)
        {
            if (!seen.Add(entry.Checksum))
                continue;

            var width = 1 << (int)((entry.Tex0 >> 26) & 0xF);
            var height = 1 << (int)((entry.Tex0 >> 30) & 0xF);
            if (width != audit.TextureWidth || height != audit.TextureHeight)
                continue;

            var candidate = ResolveCatalogPixels(entry.Checksum);
            if (candidate == null ||
                candidate.Value.Width != audit.TextureWidth ||
                candidate.Value.Height != audit.TextureHeight)
            {
                continue;
            }

            if (!TryExtractRegion(candidate.Value, audit, out var candidateRegion))
                continue;

            var (rgb, _) = ComputeMae(candidateRegion, normalizedDump);
            if (rgb < bestMae)
            {
                bestMae = rgb;
                best = entry.Checksum;
            }
        }

        return (best, bestMae);
    }

    private static string Classify(double rgbMae, double alphaMae)
    {
        if (rgbMae <= MatchMae && alphaMae <= MatchMae)
            return ClassificationMatch;
        if (rgbMae <= QuantizationMae && alphaMae <= QuantizationMae)
            return ClassificationQuantizationOnly;
        if (rgbMae <= QuantizationMae && alphaMae > AlphaProtocolMae)
            return ClassificationAlphaProtocolDiff;
        return ClassificationDivergent;
    }

    private (byte[] Rgba, int Width, int Height)? ResolveCatalogPixels(uint checksum)
    {
        if (catalogCache.TryGetValue(checksum, out var cached))
            return cached;

        (byte[] Rgba, int Width, int Height)? result = null;
        var pngBytes = catalogResolver(checksum);
        if (pngBytes != null)
        {
            using var image = Image.Load<Rgba32>(pngBytes);
            var rgba = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(rgba);
            result = (rgba, image.Width, image.Height);
        }

        catalogCache[checksum] = result;
        return result;
    }

    private static bool TryExtractRegion(
        (byte[] Rgba, int Width, int Height) catalog,
        GsTextureDumpAuditRow audit,
        out byte[] region)
    {
        region = [];
        if (audit.Width <= 0 || audit.Height <= 0 ||
            audit.RegionX < 0 || audit.RegionY < 0 ||
            audit.RegionX + audit.Width > catalog.Width ||
            audit.RegionY + audit.Height > catalog.Height)
        {
            return false;
        }

        region = new byte[audit.Width * audit.Height * 4];
        for (var y = 0; y < audit.Height; y++)
        {
            var src = ((audit.RegionY + y) * catalog.Width + audit.RegionX) * 4;
            Array.Copy(catalog.Rgba, src, region, y * audit.Width * 4, audit.Width * 4);
        }

        return true;
    }

    /// <summary>
    ///     When the GSORACLE_DEBUG_DIR environment variable names a directory,
    ///     writes each compared (catalog, dump) pair as side-by-side PNGs for
    ///     visual triage of Divergent rows — the zone-TEX census workflow's
    ///     primary eyeball tool.
    /// </summary>
    private static void WriteDebugPair(
        uint checksum,
        GsTextureDumpAuditRow audit,
        string classification,
        byte[] catalogRgba,
        byte[] dumpRgba)
    {
        var debugDir = Environment.GetEnvironmentVariable("GSORACLE_DEBUG_DIR");
        if (string.IsNullOrWhiteSpace(debugDir))
            return;

        Directory.CreateDirectory(debugDir);
        var stem = $"{checksum:X8}_{classification}_{audit.Width}x{audit.Height}_{audit.ContentHash:X8}";
        SavePng(Path.Combine(debugDir, stem + "_catalog.png"), catalogRgba, audit.Width, audit.Height);
        SavePng(Path.Combine(debugDir, stem + "_dump.png"), dumpRgba, audit.Width, audit.Height);
    }

    private static void SavePng(string path, byte[] rgba, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
        image.SaveAsPng(path);
    }

    private static byte[] ScaleAlphaToPngDomain(byte[] rgba)
    {
        var scaled = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            scaled[i] = rgba[i];
            scaled[i + 1] = rgba[i + 1];
            scaled[i + 2] = rgba[i + 2];
            scaled[i + 3] = (byte)Math.Min(255, rgba[i + 3] * 255 / 128);
        }

        return scaled;
    }

    private static ulong ParseHex(string value)
    {
        var span = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static (double RgbMae, double AlphaMae) ComputeMae(byte[] expected, byte[] actual)
    {
        var pixels = Math.Min(expected.Length, actual.Length) / 4;
        if (pixels == 0)
            return (255, 255);

        long rgb = 0;
        long alpha = 0;
        for (var i = 0; i < pixels; i++)
        {
            var o = i * 4;
            rgb += Math.Abs(expected[o] - actual[o]) +
                   Math.Abs(expected[o + 1] - actual[o + 1]) +
                   Math.Abs(expected[o + 2] - actual[o + 2]);
            alpha += Math.Abs(expected[o + 3] - actual[o + 3]);
        }

        return ((double)rgb / (pixels * 3), (double)alpha / pixels);
    }
}
