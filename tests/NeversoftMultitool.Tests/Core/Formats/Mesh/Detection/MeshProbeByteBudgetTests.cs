using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Detection;

/// <summary>
///     <see cref="MeshTypeDetector.GetProbeByteBudget" /> promises that reading
///     only that many header bytes reaches the SAME verdict as reading the whole
///     file. Nothing pinned that promise, and it was false: the Xbox-scene family
///     was budgeted 48 bytes while its own ladder needs far more —
///     <c>NgcSceneFile.IsNgcScene</c> refuses any buffer shorter than its 64-byte
///     header, and <c>ThawSceneFile.IsThawScene</c> has to reach the 0xBABEFACE
///     sentinel that sits past the entire material list. Every GameCube and THAW
///     PC scene therefore failed name-then-content detection and was reported
///     "Unsupported version", while the GUI scanner — which runs its own
///     whole-buffer check instead of coming through the detector — kept working.
///     That divergence is exactly what let it go unnoticed.
/// </summary>
public class MeshProbeByteBudgetTests(TestPaths paths)
{
    /// <summary>
    ///     Scene files whose format is decided by content, one per affected
    ///     platform family, so a regression names the platform it broke.
    /// </summary>
    public static TheoryData<string, string> SceneSamples()
    {
        return new TheoryData<string, string>
        {
            { "Tony Hawk's American Wasteland (2005-8-22, GC - Final)", "BA94BD41.skin.ngc" },
            { "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "arrow.mdl.wpc" }
        };
    }

    /// <summary>
    ///     The budget is only meaningful relative to what the predicates read, so
    ///     assert the structural minimum directly: a GameCube scene is recognised
    ///     by a sentinel inside a 64-byte header, so a smaller budget cannot see
    ///     it no matter what the file contains.
    /// </summary>
    [Theory]
    [InlineData("a.skin.ngc")]
    [InlineData("a.mdl.ngc")]
    [InlineData("a.scn.ngc")]
    [InlineData("a.skin.wpc")]
    [InlineData("a.mdl.wpc")]
    [InlineData("a.skin.xbx")]
    [InlineData("a.mdl.xbx")]
    public void XboxSceneBudget_CoversTheHeaderItsOwnLadderReads(string fileName)
    {
        // 64 = NgcSceneFile's header size, the largest fixed-offset read in the
        // ladder. The THAW branch needs more still (material-list dependent),
        // which is why the corpus test below compares against the whole file.
        Assert.True(MeshTypeDetector.GetProbeByteBudget(fileName) >= 64,
            $"{fileName}: budget {MeshTypeDetector.GetProbeByteBudget(fileName)} " +
            "cannot reach the GameCube sentinel at offset 0x2C");
    }

    /// <summary>
    ///     The actual promise: budgeted detection must agree with whole-file
    ///     detection. This is the check the docstring claimed existed.
    /// </summary>
    [CorpusTheory]
    [MemberData(nameof(SceneSamples))]
    public void BudgetedDetection_AgreesWithWholeFileDetection(string build, string fileName)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = paths.FindSampleFile(build, fileName);
        Assert.SkipWhen(path == null, $"{build}/{fileName} not present");

        var budgeted = MeshTypeDetector.Detect(path!);
        var whole = MeshTypeDetector.DetectFromBytes(
            Path.GetFileName(path!), File.ReadAllBytes(path!), new FileInfo(path!).Length);

        Assert.Equal(whole.Kind, budgeted.Kind);
        Assert.Equal(whole.DisplayFormat, budgeted.DisplayFormat);
        Assert.True(budgeted.IsSupported,
            $"{fileName} routed as unsupported: {budgeted.UnsupportedReason}");
    }
}
