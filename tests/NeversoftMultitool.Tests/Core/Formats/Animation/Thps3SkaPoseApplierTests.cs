using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class Thps3SkaPoseApplierTests(TestPaths paths)
{
    [Fact]
    public void ResolveRotation_SupportsAllDiagnosticModes()
    {
        var bind = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f));
        var raw = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.25f));
        var conjugated = Quaternion.Conjugate(raw);

        AssertQuaternionClose(
            Quaternion.Normalize(Quaternion.Multiply(bind, raw)),
            Thps3SkaPoseApplier.ResolveRotation(bind, raw, Thps3SkaRotationMode.BindRaw));
        AssertQuaternionClose(
            raw,
            Thps3SkaPoseApplier.ResolveRotation(bind, raw, Thps3SkaRotationMode.DirectRaw));
        AssertQuaternionClose(
            Quaternion.Normalize(Quaternion.Multiply(bind, conjugated)),
            Thps3SkaPoseApplier.ResolveRotation(bind, raw, Thps3SkaRotationMode.BindConjugated));
        AssertQuaternionClose(
            conjugated,
            Thps3SkaPoseApplier.ResolveRotation(bind, raw, Thps3SkaRotationMode.DirectConjugated));
    }

    [Fact]
    public void ResolveTranslation_AnchoredCancelsStaticSkaOffset_RawPreservesIt()
    {
        var bind = new Vector3(1f, 2f, 3f);
        var firstSka = new Vector3(-10f, 50f, 7f);
        var currentSka = new Vector3(-10f, 50f, 9.5f);

        Assert.Equal(
            new Vector3(1f, 2f, 5.5f),
            Thps3SkaPoseApplier.ResolveTranslation(
                bind, currentSka, firstSka, Thps3SkaTranslationMode.Anchored));
        Assert.Equal(
            currentSka,
            Thps3SkaPoseApplier.ResolveTranslation(
                bind, currentSka, firstSka, Thps3SkaTranslationMode.Raw));
    }

    [Fact]
    public void TryParse_KnowsDirectRawWithRawTranslationDiagnosticMode()
    {
        Assert.True(Thps3SkaAnimationMode.TryParse("direct-raw-rawt", out var mode, out var error), error);

        Assert.Equal(Thps3SkaRotationMode.DirectRaw, mode.RotationMode);
        Assert.Equal(Thps3SkaTranslationMode.Raw, mode.TranslationMode);
        Assert.Contains("direct-raw-rawt", Thps3SkaAnimationMode.KnownModeNames);
    }

    [Fact]
    public void AnalyzeBoneOrder_ReportsExactOrder()
    {
        var skin = CreateSkin([0, 1, 2], [0, 1, 2]);

        var report = Thps3SkaPoseApplier.AnalyzeBoneOrder(skin);

        Assert.True(report.IsExact);
        Assert.Equal(Thps3HAnimBoneOrderStatus.Exact, report.IdStatus);
        Assert.Equal(Thps3HAnimBoneOrderStatus.Exact, report.IndexStatus);
        Assert.Null(report.IdPermutation);
        Assert.Null(report.IndexPermutation);
    }

    [Fact]
    public void AnalyzeBoneOrder_ReportsUsablePermutation()
    {
        var skin = CreateSkin([2, 0, 1], [0, 1, 2]);

        var report = Thps3SkaPoseApplier.AnalyzeBoneOrder(skin);

        Assert.False(report.IsExact);
        Assert.Equal(Thps3HAnimBoneOrderStatus.UsablePermutation, report.IdStatus);
        Assert.NotNull(report.IdPermutation);
        Assert.Equal(1, report.IdPermutation[0]);
        Assert.Equal(2, report.IdPermutation[1]);
        Assert.Equal(0, report.IdPermutation[2]);
        Assert.Equal(Thps3HAnimBoneOrderStatus.Exact, report.IndexStatus);
    }

    [Fact]
    public void AnalyzeBoneOrder_ReportsInvalidMapping()
    {
        var skin = CreateSkin([0, 0, 2], [0, 4, 2]);

        var report = Thps3SkaPoseApplier.AnalyzeBoneOrder(skin);

        Assert.Equal(Thps3HAnimBoneOrderStatus.Invalid, report.IdStatus);
        Assert.Equal(Thps3HAnimBoneOrderStatus.Invalid, report.IndexStatus);
        Assert.Null(report.IdPermutation);
        Assert.Null(report.IndexPermutation);
    }

    [Theory]
    [InlineData("C:/tmp/skater_m_Idle.ska", 29, 158, 71, 1.0666667f)]
    [InlineData("C:/tmp/skater_m_AirIdle.ska", 29, 132, 69, 1.3333334f)]
    public void ParseThps3_LocalFixtures_HaveStableKeyCounts(
        string path, int expectedBones, int expectedRotationKeys, int expectedTranslationKeys, float expectedDuration)
    {
        Assert.SkipWhen(!File.Exists(path), $"Local THPS3 SKA fixture not found: {path}");

        var animation = SkaFile.Parse(File.ReadAllBytes(path));

        Assert.Equal(expectedBones, animation.BoneTracks.Length);
        Assert.Equal(expectedRotationKeys, animation.BoneTracks.Sum(static track => track.RotationKeys.Length));
        Assert.Equal(expectedTranslationKeys, animation.BoneTracks.Sum(static track => track.TranslationKeys.Length));
        Assert.True(Math.Abs(animation.Duration - expectedDuration) < 0.0001f);
    }

    [Fact]
    public void ParseThps3_LocalIdle_UsesRuntimeQTrackGrouping()
    {
        const string path = "C:/tmp/skater_m_Idle.ska";
        Assert.SkipWhen(!File.Exists(path), $"Local THPS3 SKA fixture not found: {path}");

        var animation = SkaFile.Parse(File.ReadAllBytes(path));

        Assert.Empty(animation.BoneTracks[0].RotationKeys);
        Assert.Equal(2, animation.BoneTracks[1].RotationKeys.Length);
        Assert.Equal(9, animation.BoneTracks[2].RotationKeys.Length);
        Assert.Equal(19, animation.BoneTracks[3].RotationKeys.Length);
        Assert.Equal(
            new[] { 0, 10, 16, 20, 32, 40, 44, 50, 64 },
            animation.BoneTracks[2].RotationKeys.Select(static key => (int)MathF.Round(key.Time * 60f)).ToArray());
    }

    [Fact]
    public void AnalyzeBoneOrder_LocalSkaterFixture_IsExact()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var sknPath = Path.Combine(
            paths.SampleBuildsDir!,
            "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)",
            "Extracted",
            "SKATE3",
            "pre",
            "cas_male",
            "models",
            "skater_m",
            "skater_m.skn");
        Assert.SkipWhen(!File.Exists(sknPath), $"Local THPS3 SKN fixture not found: {sknPath}");

        var clump = RwDffFile.Parse(sknPath);
        var skin = clump.Atomics.First(static atomic => atomic.SkinData != null).SkinData!;

        var report = Thps3SkaPoseApplier.AnalyzeBoneOrder(skin);

        Assert.True(report.IsExact, report.ToDisplayString());
    }

    private static RwSkinData CreateSkin(int[] ids, int[] indices)
    {
        var bones = new RwSkinBone[ids.Length];
        for (var i = 0; i < bones.Length; i++)
            bones[i] = new RwSkinBone(ids[i], indices[i], 0, Matrix4x4.Identity);

        return new RwSkinData
        {
            NumBones = bones.Length,
            NumVertices = 1,
            BoneIndices = [0, 0, 0, 0],
            BoneWeights = [1f, 0f, 0f, 0f],
            Bones = bones
        };
    }

    private static void AssertQuaternionClose(Quaternion expected, Quaternion actual, float tolerance = 1e-5f)
    {
        expected = Quaternion.Normalize(expected);
        actual = Quaternion.Normalize(actual);
        var dot = Math.Abs(Quaternion.Dot(expected, actual));
        Assert.True(
            Math.Abs(1f - dot) <= tolerance,
            $"Expected {Format(expected)}, got {Format(actual)}, dot={dot}");
    }

    private static string Format(Quaternion q)
        => $"({q.X:F6}, {q.Y:F6}, {q.Z:F6}, {q.W:F6})";
}
