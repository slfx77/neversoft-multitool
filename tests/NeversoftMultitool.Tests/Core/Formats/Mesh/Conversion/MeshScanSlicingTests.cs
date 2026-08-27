using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins the split of one scan between the Levels tab and the Meshes &amp;
///     Characters tab.
/// </summary>
public sealed class MeshScanSlicingTests
{
    private static readonly MeshLevelFacts[] Scan =
    [
        Facts("l1a1_g.psx"),                       // level  (PSX _g)
        Facts("hawk.psx"),                         // model
        Facts("skateshop.bsp"),                    // level  (RW world)
        Facts("Itm_Bonus01.ddm"),                  // model  (standalone DDM)
        Facts("mall_o.ddm") with { HasPlacedPsxCompanion = true },  // level (placed)
        Facts("0_hangar.lvl.gba"),                 // level  (GBA)
        Facts("13_spider_man.chr.gba"),            // model  (GBA character)
        Facts("SkCon.scn.xbx"),                    // level  (scene)
        Facts("skater_lasek.skin.ps2")             // model
    ];

    [Fact]
    public void TheTwoSlicesAreExactComplementsAndKeepScanOrder()
    {
        var all = Scan.Where(f => MeshScanSlicing.Includes(MeshScanSlice.All, f)).ToArray();
        var levels = Scan.Where(f => MeshScanSlicing.Includes(MeshScanSlice.Levels, f)).ToArray();
        var models = Scan.Where(f => MeshScanSlicing.Includes(MeshScanSlice.Models, f)).ToArray();

        Assert.Equal(Scan, all);
        Assert.Equal(Scan.Length, levels.Length + models.Length);
        Assert.Empty(levels.Intersect(models));

        // Order matters beyond appearances: MeshOutputPathPlanner resolves
        // colliding output stems by first-seen ordinal.
        Assert.Equal(
            ["l1a1_g.psx", "skateshop.bsp", "mall_o.ddm", "0_hangar.lvl.gba", "SkCon.scn.xbx"],
            levels.Select(f => f.FileName));
        Assert.Equal(
            ["hawk.psx", "Itm_Bonus01.ddm", "13_spider_man.chr.gba", "skater_lasek.skin.ps2"],
            models.Select(f => f.FileName));
    }

    [Fact]
    public void EveryRowLandsInExactlyOneTab()
    {
        foreach (var facts in Scan)
        {
            var inLevels = MeshScanSlicing.Includes(MeshScanSlice.Levels, facts);
            var inModels = MeshScanSlicing.Includes(MeshScanSlice.Models, facts);
            Assert.True(inLevels ^ inModels, facts.FileName);
        }
    }

    private static MeshLevelFacts Facts(string fileName) => new(
        fileName, fileName, fileName, fileName.EndsWith(".psx", StringComparison.Ordinal),
        false, false, false, PsxMeshFormatRevision.Unknown, Ps2SceneSubFormat.None,
        false, false, 0f, 0);
}
