using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public class MeshBatchOutputNamingTests
{
    [Theory]
    [InlineData(@"C:\game\SKATE3.WAD::foo.dff", "foo", "foo")]
    [InlineData(@"C:\game\SKATE3.WAD::Ap\Models\mission.col", "mission", "mission")]
    [InlineData(@"C:\game\models\geometry_007.psx.n64", "n64_007", "geometry_007")]
    [InlineData(@"C:\game\ROM.z64::models\geometry_007.psx.n64", "n64_007", "geometry_007")]
    [InlineData(@"C:\game\.col", "mesh", "mesh")]
    public void Stems_PreserveLegacyNames_AndStripArchiveContainer(
        string displayPath,
        string expectedConversionStem,
        string expectedRenderStem)
    {
        Assert.Equal(expectedConversionStem, MeshBatchOutputNaming.ConversionStem(displayPath));
        Assert.Equal(expectedRenderStem, MeshBatchOutputNaming.RenderStem(displayPath));
    }

    [Fact]
    public void Plan_NestedArchiveAndDuplicateVirtualPaths_ProducesUniqueOutputs()
    {
        string[] displayPaths =
        [
            @"C:\game\outer.wad::inner.pre::A\mission.col",
            @"C:\game\outer.wad::inner.pre::B\mission.col",
            @"C:\game\outer.wad::inner.pre::B\mission.col"
        ];

        var plan = MeshOutputPathPlanner.Plan(
            displayPaths,
            MeshBatchOutputNaming.RenderStem,
            inputRoot: null);
        var outputs = plan
            .Select(item => Path.Combine(item.Subdirectory, item.Stem))
            .ToArray();

        Assert.Equal(
        [
            Path.Combine("A", "mission"),
            Path.Combine("B", "mission"),
            Path.Combine("B", "mission_2")
        ], outputs);
    }
}
