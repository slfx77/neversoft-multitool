using NeversoftMultitool.Core.Formats.GsDump.Oracle;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.GsDump.Oracle;

/// <summary>
///     Ratchet over the committed texoracle goldens: the zone-TEX decode may
///     never diverge from the replay's runtime ground truth beyond the
///     documented allowlist, and the TEX0↔checksum correlation coverage may
///     not silently collapse. The current allowlist is the Phase-3 zone-TEX
///     census worklist — every entry is either a slot-attribution error (the
///     game streams multiple assets through one TEX0; see SlotReuseSuspect)
///     or a genuine decode lead, and each entry removed is progress.
/// </summary>
public sealed class ThawZoneTexOracleTests
{
    /// <summary>
    ///     The TRUE zone-TEX decode leads (2026-07-27): after content-based
    ///     attribution, only these two checksums diverge from the replay's
    ///     runtime ground truth with THEMSELVES as their own best content
    ///     match — i.e. the same asset decoded differently, not a slot-reuse
    ///     attribution artifact. (The original 43-entry list collapsed: 311
    ///     rows were streamed out-of-catalog content, 7 were same-zone slot
    ///     swaps.) These two are the Phase-3 A1 decode worklist; every entry
    ///     removed is progress, every NEW divergence fails this ratchet.
    /// </summary>
    private static readonly HashSet<uint> DivergenceAllowlist =
    [
        0x0935DD38, // rgbMae ~19.9, 3 captures — palette tint or slot-bias suspect
        0xE6ABDEED // rgbMae ~11.8, 1 capture
    ];

    [Fact]
    public void NoNewDivergentDecodes()
    {
        Assert.SkipWhen(!GsOracleGoldenData.HasGoldens,
            "GS-oracle goldens absent — regenerate with tools/diagnostics/build_gsoracle.ps1.");

        var offenders = new List<string>();
        foreach (var (tag, report) in GsOracleGoldenData.LoadTextureOracleReports())
        {
            foreach (var row in report.Rows)
            {
                if (row.Classification == GsTextureOracleComparer.ClassificationDivergent &&
                    !DivergenceAllowlist.Contains(row.Checksum))
                {
                    offenders.Add(
                        $"{tag}: 0x{row.Checksum:X8} {row.Source} rgbMae={row.RgbMae} " +
                        $"aMae={row.AlphaMae} {row.Width}x{row.Height} psm=0x{row.Psm:X2}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} NEW divergent zone-TEX decode(s) beyond the allowlist:\n" +
            string.Join('\n', offenders.Take(20)));
    }

    [Fact]
    public void CorrelationCoverageDoesNotCollapse()
    {
        Assert.SkipWhen(!GsOracleGoldenData.HasGoldens,
            "GS-oracle goldens absent — regenerate with tools/diagnostics/build_gsoracle.ps1.");

        // Seeded coverage over the z_bh catalog sits at 22-29% of textured
        // draws per THAW capture (mixed scenes; the catalog is one zone). The
        // floor guards the correlation machinery, not scene content: a
        // resolver regression that hollows the oracle to near-zero must fail.
        const double floor = 0.10;
        var failures = new List<string>();
        foreach (var (tag, report) in GsOracleGoldenData.LoadOracleReports())
        {
            if (!report.Serial.Contains("21295", StringComparison.Ordinal))
                continue; // THPG captures carry no THAW zone catalog coverage.

            if (report.Coverage.ResolvedDrawFraction < floor)
            {
                failures.Add(
                    $"{tag}: resolved draw fraction {report.Coverage.ResolvedDrawFraction:P1} " +
                    $"below the {floor:P0} floor " +
                    $"({report.Coverage.ResolvedDraws}/{report.Coverage.TexturedDraws} draws)");
            }
        }

        Assert.True(failures.Count == 0,
            "TEX0-to-checksum correlation coverage collapsed:\n" + string.Join('\n', failures));
    }
}
