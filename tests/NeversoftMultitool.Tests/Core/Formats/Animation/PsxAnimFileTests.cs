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

        // Carnage ships a 0x2C (DecompressStream-compressed) hier/anim chunk
        // with 39 declared streams. The previous parser saw 44 only because it
        // started reading at meshBlockEnd which was 8 bytes before the actual
        // chunk data — the off-by-8 misalignment treated the chunk tag (0x2C
        // = 44) as numStreams.
        Assert.Equal(PsxAnimLayoutVariant.Monolithic, animFile.Layout);
        Assert.Equal(PsxAnimationFormatRevision.CompressedV2, animFile.FormatRevision);
        Assert.Equal(PsxMeshFile.HierChunkV2Tag, animFile.ChunkTag);
        Assert.Equal(PsxCharacterRuntimeRevision.ClassicSuper, animFile.MinimumRuntimeRevision);
        Assert.False(animFile.RequiresExtendedAnimationSlotIndex);
        Assert.Equal(39, animFile.NumStreamsDeclared);
        Assert.True(animFile.Entries.Count >= 30,
            $"expected at least 30 valid entries, got {animFile.Entries.Count}");
        Assert.True(animFile.Pool.Length > 0);
        Assert.Equal(19, psxFile.Objects.Count);

        // Anim 0 is the 30-frame opener (the diagnostic md's anim 1 under the
        // old off-by-8 numbering).
        Assert.Equal(316, animFile.Entries[0].PoolOffset);
        Assert.Equal(30, animFile.Entries[0].FrameCount);
        Assert.Equal(0, animFile.Entries[0].TweenFlag);
    }

    [Fact]
    public void Parse_BlackcatPsx_DetectsMonolithicLayout()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "blackcat.psx");
        Assert.SkipWhen(path == null, "blackcat.psx not found in sample builds");

        var (_, animFile) = ParseAnimFile(path!);

        // Blackcat: 14 declared streams (not 44 — see the carnage comment for
        // why the old number was an off-by-8 misread). All 14 validate cleanly
        // now that frameCount/tweenFlag are split into separate u16s.
        Assert.Equal(PsxAnimLayoutVariant.Monolithic, animFile.Layout);
        Assert.Equal(PsxAnimationFormatRevision.CompressedV2, animFile.FormatRevision);
        Assert.Equal(PsxCharacterRuntimeRevision.ClassicSuper, animFile.MinimumRuntimeRevision);
        Assert.Equal(14, animFile.NumStreamsDeclared);
        Assert.True(animFile.Entries.Count >= 10,
            $"expected at least 10 valid entries, got {animFile.Entries.Count}");
    }

    [Fact]
    public void Parse_Hawk2Psx_DetectsDirectMatrixLayout()
    {
        var path = paths.FindSampleFile(Thps2ProtoBuild, "hawk2.psx");
        Assert.SkipWhen(path == null, "hawk2.psx not found in sample builds");

        var (psxFile, animFile) = ParseAnimFile(path!);

        // THPS2 prototype hawk2.psx ships its anim data in a v1 (0x2A) chunk:
        // 19 bones × 29 frames × 24 bytes per SMatrix = 13,224 bytes, exactly
        // the chunk payload size after the 12-byte table. The previous parser's
        // "PrototypeSparse" classification was a side effect of reading 8 bytes
        // before the chunk data — those bytes were the chunk header (tag=0x2A=42,
        // size=13236) which happened to look like a sparse table header.
        Assert.Equal(PsxAnimLayoutVariant.DirectMatrix, animFile.Layout);
        Assert.Equal(PsxAnimationFormatRevision.DirectMatrixV1, animFile.FormatRevision);
        Assert.Equal(PsxMeshFile.HierChunkV1Tag, animFile.ChunkTag);
        Assert.Equal(PsxCharacterRuntimeRevision.ClassicSuper, animFile.MinimumRuntimeRevision);
        Assert.False(animFile.RequiresExtendedAnimationSlotIndex);
        Assert.True(animFile.IsDirectMatrix);
        Assert.Single(animFile.Entries);
        Assert.Equal(1, animFile.NumStreamsDeclared);
        Assert.Equal(12, animFile.Entries[0].PoolOffset);
        Assert.Equal(29, animFile.Entries[0].FrameCount);
        Assert.Equal(0, animFile.Entries[0].TweenFlag);
        Assert.Equal(19, psxFile.Objects.Count);
    }

    [Fact]
    public void Parse_CrocPsx_DetectsDirectMatrixLayout()
    {
        var path = paths.FindSampleFile(ApocalypseBuild, "croc.psx");
        Assert.SkipWhen(path == null, "croc.psx not found in sample builds");

        var (_, animFile) = ParseAnimFile(path!);

        // Apocalypse character files ship as v1 hier/anim (chunk tag 0x2A) —
        // uncompressed 24-byte SMatrix per bone per frame. Previously the
        // parser saw the tween-flag bits encoded into the upper half of the
        // u32 "frameCount" and rejected every entry past the first as bogus.
        Assert.Equal(PsxAnimLayoutVariant.DirectMatrix, animFile.Layout);
        Assert.Equal(PsxAnimationFormatRevision.DirectMatrixV1, animFile.FormatRevision);
        Assert.Equal(PsxCharacterRuntimeRevision.ClassicSuper, animFile.MinimumRuntimeRevision);
        Assert.True(animFile.IsDirectMatrix);
        Assert.True(animFile.Entries.Count >= 1, "at least one entry should be recoverable");
    }

    [Fact]
    public void Parse_SpideyPsx_DetectsExtendedAnimationSlots()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "spidey.psx");
        Assert.SkipWhen(path == null, "spidey.psx not found in sample builds");

        var (psxFile, animFile) = ParseAnimFile(path!);

        Assert.Equal(PsxAnimLayoutVariant.Monolithic, animFile.Layout);
        Assert.Equal(PsxAnimationFormatRevision.CompressedV2ExtendedSlots, animFile.FormatRevision);
        Assert.Equal(PsxMeshFile.HierChunkV2Tag, animFile.ChunkTag);
        Assert.Equal(PsxCharacterRuntimeRevision.ExtendedAnimSlots, animFile.MinimumRuntimeRevision);
        Assert.True(animFile.RequiresExtendedAnimationSlotIndex);
        Assert.Equal(300, animFile.NumStreamsDeclared);
        Assert.Equal(300, animFile.Entries.Count);
        Assert.Equal(18, psxFile.Objects.Count);
    }

    [Fact]
    public void Parse_EmptyData_ReturnsNull()
    {
        // No metaTop pointer / no chunks ⇒ TryGetAnimChunkTag fails ⇒ Parse
        // returns null. Replaces the obsolete meshBlockEnd-based signature.
        var data = new byte[16];
        var result = PsxAnimFile.Parse(data, boneCount: 19);
        Assert.Null(result);
    }

    private static (PsxMeshFile psxFile, PsxAnimFile animFile) ParseAnimFile(string path)
    {
        var data = File.ReadAllBytes(path);
        var psxFile = PsxMeshFile.Parse(data);
        Assert.NotNull(psxFile);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count);
        Assert.NotNull(animFile);
        return (psxFile, animFile);
    }
}
