using System.Globalization;

namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     Inverts the replay's per-state-bucket material audit
///     (<see cref="GsRenderAudit.Materials" />) into per-texture-checksum
///     oracle facts, joining runtime TEX0 values through the zone-texture
///     catalog's resolver. Deterministic ordering throughout so committed
///     golden JSONs do not churn between regenerations.
/// </summary>
internal static class GsOracleReportBuilder
{
    public static GsOracleReport Build(
        string capture,
        GsDumpFile dump,
        GsRenderAudit render,
        Func<ulong, uint> resolveChecksum)
    {
        var byChecksum = new Dictionary<uint, List<(ulong Tex0, GsMaterialAuditRow Row)>>();
        var unresolved = new Dictionary<string, GsOracleUnresolvedTex0>();
        var texturedBuckets = 0;
        var resolvedBuckets = 0;
        long texturedDraws = 0;
        long resolvedDraws = 0;
        long totalDraws = 0;

        foreach (var row in render.Materials)
        {
            totalDraws += row.Draws;
            if (!row.TextureEnabled)
                continue;

            texturedBuckets++;
            texturedDraws += row.Draws;

            var tex0 = ParseHex(row.Tex0);
            var checksum = resolveChecksum(tex0);
            if (checksum == 0)
            {
                if (!unresolved.TryGetValue(row.Tex0, out var miss))
                {
                    unresolved[row.Tex0] = new GsOracleUnresolvedTex0
                    {
                        Tex0 = row.Tex0,
                        Draws = row.Draws,
                        PixelsWritten = row.PixelsWritten
                    };
                }
                else
                {
                    unresolved[row.Tex0] = new GsOracleUnresolvedTex0
                    {
                        Tex0 = row.Tex0,
                        Draws = miss.Draws + row.Draws,
                        PixelsWritten = miss.PixelsWritten + row.PixelsWritten
                    };
                }

                continue;
            }

            resolvedBuckets++;
            resolvedDraws += row.Draws;
            if (!byChecksum.TryGetValue(checksum, out var list))
                byChecksum[checksum] = list = [];
            list.Add((tex0, row));
        }

        var textures = new List<GsOracleTextureFacts>(byChecksum.Count);
        foreach (var (checksum, rows) in byChecksum.OrderBy(static kv => kv.Key))
        {
            var buckets = rows
                .OrderBy(static entry => entry.Row.FirstDrawIndex)
                .ThenBy(static entry => entry.Row.Key, StringComparer.Ordinal)
                .Select(static entry => MakeBucket(entry.Row))
                .ToList();
            textures.Add(new GsOracleTextureFacts
            {
                Checksum = checksum,
                Tex0Values = rows
                    .Select(static entry => entry.Row.Tex0)
                    .Distinct()
                    .Order(StringComparer.Ordinal)
                    .ToList(),
                StateBuckets = buckets,
                TotalDraws = rows.Sum(static entry => entry.Row.Draws),
                TotalPixelsWritten = rows.Sum(static entry => entry.Row.PixelsWritten)
            });
        }

        return new GsOracleReport
        {
            Capture = capture,
            Serial = dump.Serial,
            Crc = dump.Crc,
            TotalDraws = totalDraws,
            Coverage = new GsOracleCoverage
            {
                TexturedStateBuckets = texturedBuckets,
                ResolvedStateBuckets = resolvedBuckets,
                TexturedDraws = texturedDraws,
                ResolvedDraws = resolvedDraws,
                ResolvedDrawFraction = texturedDraws == 0 ? 0 : (double)resolvedDraws / texturedDraws,
                UnresolvedTex0 = unresolved.Values
                    .OrderByDescending(static row => row.Draws)
                    .ThenBy(static row => row.Tex0, StringComparer.Ordinal)
                    .Take(64)
                    .ToList()
            },
            Textures = textures
        };
    }

    private static GsOracleStateBucket MakeBucket(GsMaterialAuditRow row)
    {
        return new GsOracleStateBucket
        {
            Primitive = row.Primitive,
            AlphaBlendEnabled = row.AlphaBlendEnabled,
            AlphaA = row.AlphaA,
            AlphaB = row.AlphaB,
            AlphaC = row.AlphaC,
            AlphaD = row.AlphaD,
            AlphaFix = row.AlphaFix,
            AlphaTestEnabled = row.AlphaTestEnabled,
            AlphaTestMethod = row.AlphaTestMethod,
            AlphaRef = row.AlphaRef,
            AlphaFailMode = row.AlphaFailMode,
            TexaTa0 = row.TexaTa0,
            TexaAem = row.TexaAem,
            TexaTa1 = row.TexaTa1,
            TextureTfx = row.TextureTfx,
            TextureTcc = row.TextureTcc,
            FramebufferMask = row.FramebufferMask,
            FramebufferPsm = row.FramebufferPsm,
            DepthTestEnabled = row.DepthTestEnabled,
            DepthTestMethod = row.DepthTestMethod,
            ZMask = row.Zmask,
            FramebufferAlphaWriteEnabled = row.FramebufferAlphaWriteEnabled,
            Draws = row.Draws,
            PixelsWritten = row.PixelsWritten,
            FirstDrawIndex = row.FirstDrawIndex,
            LastDrawIndex = row.LastDrawIndex
        };
    }

    private static ulong ParseHex(string value)
    {
        var span = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
