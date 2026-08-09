using NeversoftMultitool.Core.Formats.GsDump.Oracle;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.GsDump.Oracle;

/// <summary>
///     Adjudicates the worldzone material classifier against the GS states the
///     game ACTUALLY drew with (the committed oracle goldens): every observed
///     blend register, replayed through
///     <see cref="Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode" />, must
///     land in a classification consistent with the register-level truth. This
///     is how converter blend claims are VERIFIED rather than merely
///     implemented — hardware truth in, classifier verdict out.
/// </summary>
public sealed class WorldzoneOracleTests
{
    [Fact]
    public void Classifier_AgreesWithEveryObservedBlendState()
    {
        Assert.SkipWhen(!GsOracleGoldenData.HasGoldens,
            "GS-oracle goldens absent — regenerate with tools/validation/gsdump/build_gsoracle.ps1.");

        var failures = new List<string>();
        foreach (var (tag, report) in GsOracleGoldenData.LoadOracleReports())
        {
            foreach (var texture in report.Textures)
            {
                foreach (var bucket in texture.StateBuckets)
                {
                    var verdict = ClassifyBucket(bucket);
                    var expectation = ExpectedClassification(bucket);
                    if (expectation != null && verdict != expectation)
                    {
                        failures.Add(
                            $"{tag} 0x{texture.Checksum:X8}: alpha=({bucket.AlphaA},{bucket.AlphaB}," +
                            $"{bucket.AlphaC},{bucket.AlphaD}) fix={bucket.AlphaFix} abe={bucket.AlphaBlendEnabled} " +
                            $"atst={bucket.AlphaTestMethod}/{bucket.AlphaRef} -> classifier said {verdict}, " +
                            $"register truth expects {expectation} ({bucket.Draws} draws)");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} observed GS state(s) contradict the classifier:\n" +
            string.Join('\n', failures.Take(20)));
    }

    /// <summary>
    ///     The register-level truth for states with an unambiguous expectation.
    ///     Returns null for states the classifier legitimately resolves with
    ///     context this test does not model (destination-alpha, fixed-blend
    ///     thresholds, alpha-test masking interplay).
    /// </summary>
    private static string? ExpectedClassification(GsOracleStateBucket bucket)
    {
        if (!bucket.AlphaBlendEnabled)
            return null;

        var additive = bucket is { AlphaA: 0, AlphaB: 2, AlphaD: 1 };
        var subtractive = bucket is { AlphaA: 2, AlphaB: 0, AlphaD: 1 };
        var standardBlend = bucket is { AlphaA: 0, AlphaB: 1, AlphaC: 0, AlphaD: 1 };
        if (additive || subtractive || standardBlend)
            return "BLEND";

        return null;
    }

    private static string ClassifyBucket(GsOracleStateBucket bucket)
    {
        return Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(new Ps2GeomLeaf
        {
            Vertices = [],
            DmaAlpha1 = ReconstructAlphaRegister(bucket),
            DmaTest1 = ReconstructTestRegister(bucket)
        });
    }

    private static ulong ReconstructAlphaRegister(GsOracleStateBucket bucket)
    {
        return bucket.AlphaA
               | (ulong)bucket.AlphaB << 2
               | (ulong)bucket.AlphaC << 4
               | (ulong)bucket.AlphaD << 6
               | (ulong)bucket.AlphaFix << 32;
    }

    private static ulong ReconstructTestRegister(GsOracleStateBucket bucket)
    {
        return (bucket.AlphaTestEnabled ? 1u : 0u)
               | (ulong)bucket.AlphaTestMethod << 1
               | (ulong)bucket.AlphaRef << 4
               | (ulong)bucket.AlphaFailMode << 12;
    }
}
