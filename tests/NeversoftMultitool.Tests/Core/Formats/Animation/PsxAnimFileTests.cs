using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public class PsxAnimFileTests(TestPaths paths)
{
    private const string SpiderManBuild = "Spider-Man (2000-9-1, PSX - Final)";
    private const string Thps2ProtoBuild = "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)";
    private const string ApocalypseBuild = "Apocalypse (1998-11-17, PSX - Final)";

    [Fact]
    public void Parse_CarnagePsx_DetectsMonolithicLayout()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "carnage.psx");
        Assert.SkipWhen(path == null, "carnage.psx not found in sample builds");

        var (psxFile, animFile) = ParseAnimFile(path!);

        Assert.Equal(PsxAnimLayoutVariant.Monolithic, animFile.Layout);
        Assert.Equal(44, animFile.NumStreamsDeclared);
        // Diagnostic: 40/44 entries pass strict validation; the remaining 4
        // have garbage poolOffset/frameCount values past the end of the table.
        Assert.Equal(40, animFile.Entries.Count);
        Assert.True(animFile.Pool.Length > 0);
        Assert.Equal(19, psxFile.Objects.Count);
    }

    [Fact]
    public void Parse_BlackcatPsx_DetectsMonolithicLayout()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "blackcat.psx");
        Assert.SkipWhen(path == null, "blackcat.psx not found in sample builds");

        var (_, animFile) = ParseAnimFile(path!);

        Assert.Equal(PsxAnimLayoutVariant.Monolithic, animFile.Layout);
        Assert.Equal(44, animFile.NumStreamsDeclared);
        // Blackcat under-fills: only ~16 of the 44 declared streams have real
        // entries; the rest is slack with garbage offsets. Per the diagnostic doc.
        Assert.True(animFile.Entries.Count >= 10,
            $"expected at least 10 valid entries, got {animFile.Entries.Count}");
    }

    [Fact]
    public void Parse_Hawk2Psx_DetectsPrototypeSparseLayout()
    {
        var path = paths.FindSampleFile(Thps2ProtoBuild, "hawk2.psx");
        Assert.SkipWhen(path == null, "hawk2.psx not found in sample builds");

        var (_, animFile) = ParseAnimFile(path!);

        // THPS2 prototype — sparse table; only the first entry is recoverable.
        // Prototype signature: u32 at +0x04 = 13,236 (poolByteSize) which
        // exceeds the pool size under monolithic interpretation.
        Assert.Equal(PsxAnimLayoutVariant.PrototypeSparse, animFile.Layout);
        Assert.Single(animFile.Entries);
        Assert.Equal(42, animFile.NumStreamsDeclared);
        // Per the diagnostic md, hawk2 anim 0 is a 1-frame pose at pool offset 12.
        Assert.Equal(1, animFile.Entries[0].FrameCount);
        Assert.Equal(12, animFile.Entries[0].PoolOffset);
    }

    [Fact]
    public void Parse_CrocPsx_DoesNotCrash()
    {
        var path = paths.FindSampleFile(ApocalypseBuild, "croc.psx");
        Assert.SkipWhen(path == null, "croc.psx not found in sample builds");

        var (_, animFile) = ParseAnimFile(path!);

        // Apocalypse v3 character files use the same monolithic layout as
        // later games — just with a different file Version field.
        Assert.True(animFile.Entries.Count >= 1, "at least one entry should be recoverable");
    }

    [Fact]
    public void Parse_FilesWithNoMeshBoundary_ReturnsNull()
    {
        // Synthetic empty data — meshBlockEnd of 0 is invalid.
        var data = new byte[16];
        var result = PsxAnimFile.Parse(data, boneCount: 19, meshBlockEnd: 0);
        Assert.Null(result);
    }

    private static (PsxMeshFile psxFile, PsxAnimFile animFile) ParseAnimFile(string path)
    {
        var data = File.ReadAllBytes(path);
        var psxFile = PsxMeshFile.Parse(data);
        Assert.NotNull(psxFile);
        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        Assert.True(meshBlockEnd > 0);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd);
        Assert.NotNull(animFile);
        return (psxFile, animFile);
    }
}
