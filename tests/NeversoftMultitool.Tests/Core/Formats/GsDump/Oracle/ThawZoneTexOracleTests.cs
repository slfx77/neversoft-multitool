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
    ///     Checksums with Divergent rows in the seeded goldens (2026-07-27,
    ///     z_bh catalog over all 17 captures). Confirmed by pair-PNG triage to
    ///     be dominated by dynamic-slot attribution (a car atlas attributed
    ///     where jeans were uploaded, at the shadow-saga slot) — the Phase-3
    ///     content-based attribution census works this list down; every entry
    ///     removed is progress, every NEW divergence fails this ratchet.
    /// </summary>
    private static readonly HashSet<uint> DivergenceAllowlist =
    [
        0x027931EA, 0x0935DD38, 0x0BA6EDB7, 0x0BBDF569, 0x0D3F5B6B,
        0x132126FE, 0x15766597, 0x1CFD7D44, 0x1DBD5C16, 0x26528542,
        0x2DC8C0C1, 0x331B1ADF, 0x3FCFA802, 0x3FD61FB0, 0x4A1F8294,
        0x5F55A56D, 0x60C5FF70, 0x674E6595, 0x689FC338, 0x6CC9E390,
        0x735A5573, 0x73896568, 0x77B46C84, 0x82B70E98, 0x8C1466EA,
        0x94CEFF64, 0x9B8B3FB4, 0xA57B31F2, 0xA99B9EBA, 0xAD458276,
        0xAE68AB14, 0xBE23EFD8, 0xC1BC1EE8, 0xC406D7C1, 0xCEE89221,
        0xD0605E66, 0xD87BF374, 0xE6ABDEED, 0xEEDE3797, 0xF510CFDC,
        0xF6952B0D, 0xFCF04806, 0xFE57D049
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
