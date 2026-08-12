using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public class MeshOutputPathPlannerTests
{
    private static string Stem(string file)
    {
        return MeshTypeDetector.GetStem(file);
    }

    [Fact]
    public void Plan_UniqueStems_KeepsTheFlatLayout()
    {
        string[] files =
        [
            @"C:\game\levels\ap\ap.col",
            @"C:\game\levels\bh\downtown.col"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, @"C:\game");

        Assert.All(plan, p => Assert.Equal("", p.Subdirectory));
        Assert.Equal(["ap", "downtown"], plan.Select(p => p.Stem).OrderBy(s => s));
    }

    [Fact]
    public void Plan_SingleFile_IsAlwaysFlat()
    {
        var plan = MeshOutputPathPlanner.Plan([@"C:\game\a\mission.col"], Stem, @"C:\game");

        Assert.Equal("", plan[0].Subdirectory);
        Assert.Equal("mission", plan[0].Stem);
    }

    [Fact]
    public void Plan_CollidingStems_MirrorTheSourceFolders()
    {
        // The real corpus shape: 1,228 files literally named mission.col.
        string[] files =
        [
            @"C:\game\missions\m_c1_demo\mission.col",
            @"C:\game\missions\m_c1_film1\mission.col",
            @"C:\game\missions\m_c2_ntg\mission.col"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, @"C:\game");

        Assert.All(plan, p => Assert.Equal("mission", p.Stem));
        Assert.Equal(
            [
                Path.Combine("missions", "m_c1_demo"),
                Path.Combine("missions", "m_c1_film1"),
                Path.Combine("missions", "m_c2_ntg")
            ],
            plan.Select(p => p.Subdirectory).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Plan_AlwaysProducesABijection()
    {
        // Same stem AND same directory — the mirrored path cannot separate these,
        // so the ordinal backstop must.
        string[] files =
        [
            @"C:\game\pak\mission.col",
            @"C:\game\pak\mission.col.ps2",
            @"C:\game\pak\mission.col.xbx"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, @"C:\game");
        var outputs = plan.Select(p => Path.Combine(p.Subdirectory, p.Stem)).ToList();

        Assert.Equal(3, outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Plan_OrdinalBackstop_DoesNotStealAnotherGroupsPreferredStem()
    {
        string[] files =
        [
            @"C:\game\a\mission.col",
            @"C:\game\a\mission.col.ps2",
            @"C:\game\a\mission_2.col",
            @"C:\game\b\mission_2.col"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, @"C:\game");
        var byFile = plan.ToDictionary(
            static item => item.File,
            static item => Path.Combine(item.Subdirectory, item.Stem));

        Assert.Equal(Path.Combine("a", "mission"), byFile[files[0]]);
        Assert.Equal(Path.Combine("a", "mission_3"), byFile[files[1]]);
        Assert.Equal(Path.Combine("a", "mission_2"), byFile[files[2]]);
        Assert.Equal(Path.Combine("b", "mission_2"), byFile[files[3]]);
        Assert.Equal(
            files.Length,
            byFile.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Plan_ExpandedOutputs_ReservesDerivedAndNaturalAliasesGlobally()
    {
        string[] files =
        [
            @"C:\game\same\foo.tex",
            @"C:\game\same\foo_mip1.img",
            @"C:\game\else\unique.img"
        ];

        static string PreferredStem(string file)
        {
            return Path.GetFileNameWithoutExtension(file);
        }

        static IReadOnlyList<string> OutputStems(string file, string proposedStem)
        {
            return Path.GetFileName(file).Equals("foo.tex", StringComparison.OrdinalIgnoreCase)
                ? [proposedStem, $"{proposedStem}_mip1"]
                : [proposedStem];
        }

        var plan = MeshOutputPathPlanner.Plan(
            files,
            PreferredStem,
            OutputStems,
            @"C:\game");
        var byFile = plan.ToDictionary(static item => item.File);

        Assert.Equal("same", byFile[files[0]].Subdirectory);
        Assert.Equal("foo", byFile[files[0]].Stem);
        Assert.Equal("same", byFile[files[1]].Subdirectory);
        Assert.Equal("foo_mip1_2", byFile[files[1]].Stem);
        Assert.Equal("", byFile[files[2]].Subdirectory);
        Assert.Equal("unique", byFile[files[2]].Stem);

        var outputKeys = plan
            .SelectMany(item => OutputStems(item.File, item.Stem)
                .Select(stem => Path.Combine(item.Subdirectory, stem)))
            .ToList();
        Assert.Equal(
            outputKeys.Count,
            outputKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Plan_ExpandedOutputs_RejectsMalformedAliasSets()
    {
        string[] files = [@"C:\game\mission.col"];

        Assert.Throws<ArgumentException>(() => MeshOutputPathPlanner.Plan(
            files,
            Stem,
            static (_, _) => null!,
            @"C:\game"));
        Assert.Throws<ArgumentException>(() => MeshOutputPathPlanner.Plan(
            files,
            Stem,
            static (_, _) => [],
            @"C:\game"));
        Assert.Throws<ArgumentException>(() => MeshOutputPathPlanner.Plan(
            files,
            Stem,
            static (_, proposedStem) => [proposedStem, proposedStem.ToUpperInvariant()],
            @"C:\game"));
        Assert.Throws<ArgumentException>(() => MeshOutputPathPlanner.Plan(
            files,
            Stem,
            static (_, proposedStem) => [proposedStem, " "],
            @"C:\game"));
    }

    [Fact]
    public void Plan_ExpandedOutputs_NonVaryingDerivedAliasFailsBoundedly()
    {
        string[] files =
        [
            @"C:\game\pak\mission.col",
            @"C:\game\pak\mission.col.ps2"
        ];

        Assert.Throws<InvalidOperationException>(() => MeshOutputPathPlanner.Plan(
            files,
            Stem,
            static (_, proposedStem) => [proposedStem, "fixed"],
            @"C:\game"));
    }

    [Fact]
    public void Plan_IsDeterministic_RegardlessOfEnumerationOrder()
    {
        string[] forward =
        [
            @"C:\game\a\mission.col",
            @"C:\game\b\mission.col",
            @"C:\game\c\mission.col"
        ];
        var reversed = forward.Reverse().ToArray();

        var first = MeshOutputPathPlanner.Plan(forward, Stem, @"C:\game")
            .ToDictionary(p => p.File, p => Path.Combine(p.Subdirectory, p.Stem));
        var second = MeshOutputPathPlanner.Plan(reversed, Stem, @"C:\game")
            .ToDictionary(p => p.File, p => Path.Combine(p.Subdirectory, p.Stem));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Plan_DeterministicAllocation_PreservesCallerIterationOrder()
    {
        string[] files =
        [
            @"C:\game\pak\mission.col.xbx",
            @"C:\game\unique.col",
            @"C:\game\pak\mission.col",
            @"C:\game\pak\mission.col.ps2"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, @"C:\game");

        Assert.Equal(files, plan.Select(static item => item.File));
    }

    [Fact]
    public void Plan_ArchiveVirtualPath_ContributesBothHalves()
    {
        string[] files =
        [
            @"C:\game\SKATE3.WAD::Ap\Models\stat_point.dff",
            @"C:\game\SKATE3.WAD::Burn\Models\stat_point.dff"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, @"C:\game");

        Assert.All(plan, p => Assert.Equal("stat_point", p.Stem));
        Assert.All(plan, p => Assert.Contains("SKATE3.WAD", p.Subdirectory));
        Assert.Equal(2, plan.Select(p => p.Subdirectory).Distinct().Count());
    }

    [Fact]
    public void Plan_WithoutARoot_FallsBackToTheImmediateParent()
    {
        string[] files =
        [
            @"C:\one\mission.col",
            @"C:\two\mission.col"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, null);

        Assert.Equal(["one", "two"], plan.Select(p => p.Subdirectory).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Plan_SubdirectoryIsAlwaysASafeRelativePath()
    {
        string[] files =
        [
            @"C:\game\a\..\b\mission.col",
            @"C:\game\c\mission.col"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, null);

        Assert.All(plan, p =>
        {
            Assert.False(Path.IsPathRooted(p.Subdirectory));
            Assert.DoesNotContain("..", p.Subdirectory);
            Assert.DoesNotContain(":", p.Subdirectory);
        });
    }

    [Fact]
    public void Plan_EveryInputAppearsExactlyOnce()
    {
        string[] files =
        [
            @"C:\game\a\mission.col",
            @"C:\game\b\mission.col",
            @"C:\game\c\unique.col"
        ];

        var plan = MeshOutputPathPlanner.Plan(files, Stem, @"C:\game");

        Assert.Equal(files.Length, plan.Count);
        Assert.Equal(files.Order(), plan.Select(p => p.File).Order());
    }
}
