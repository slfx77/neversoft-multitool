using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class PsxFormatRevisionCrossGameTests(TestPaths paths)
{
    private const string Thps1ProtoBuild = "Tony Hawk's Pro Skater (1999-4-4, PSX - Prototype)";
    private const string Thps1FinalBuild = "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)";
    private const string Thps2ProtoBuild = "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)";
    private const string Thps2FinalBuild = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private const string EnterElectroFinalBuild = "Spider-Man 2 - Enter Electro (2001-8-15, PSX - Final)";

    [Theory]
    [InlineData(
        Thps1ProtoBuild,
        "burnq.psx",
        PsxMeshFormatRevision.NeversoftV3,
        PsxAnimLayoutVariant.Monolithic,
        PsxAnimationFormatRevision.CompressedV2,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV2Tag,
        65,
        65,
        19)]
    [InlineData(
        Thps1ProtoBuild,
        "hawk2_fe.psx",
        PsxMeshFormatRevision.NeversoftV3,
        PsxAnimLayoutVariant.PrototypeSparse,
        PsxAnimationFormatRevision.CompressedV2PrototypeSparse,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV2Tag,
        1,
        1,
        19)]
    [InlineData(
        Thps1ProtoBuild,
        "rasta_fe.psx",
        PsxMeshFormatRevision.NeversoftV3,
        PsxAnimLayoutVariant.DirectMatrix,
        PsxAnimationFormatRevision.DirectMatrixV1,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV1Tag,
        1,
        1,
        19)]
    [InlineData(
        Thps1FinalBuild,
        "hawk.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.Monolithic,
        PsxAnimationFormatRevision.CompressedV2,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV2Tag,
        78,
        78,
        19)]
    [InlineData(
        Thps1FinalBuild,
        "burnq_fe.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.PrototypeSparse,
        PsxAnimationFormatRevision.CompressedV2PrototypeSparse,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV2Tag,
        1,
        1,
        19)]
    [InlineData(
        Thps1FinalBuild,
        "c_cable.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.DirectMatrix,
        PsxAnimationFormatRevision.DirectMatrixV1,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV1Tag,
        1,
        1,
        5)]
    [InlineData(
        Thps2ProtoBuild,
        "mullen.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.DirectMatrix,
        PsxAnimationFormatRevision.DirectMatrixV1,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV1Tag,
        1,
        1,
        19)]
    [InlineData(
        Thps2ProtoBuild,
        "sk2anim.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.Monolithic,
        PsxAnimationFormatRevision.CompressedV2,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV2Tag,
        147,
        147,
        19)]
    [InlineData(
        Thps2FinalBuild,
        "sk2anim.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.Monolithic,
        PsxAnimationFormatRevision.CompressedV2,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV2Tag,
        218,
        218,
        19)]
    [InlineData(
        EnterElectroFinalBuild,
        "spidey.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.Monolithic,
        PsxAnimationFormatRevision.CompressedV2ExtendedSlots,
        PsxCharacterRuntimeRevision.ExtendedAnimSlots,
        PsxMeshFile.HierChunkV2Tag,
        300,
        300,
        18)]
    [InlineData(
        EnterElectroFinalBuild,
        "beast.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.Monolithic,
        PsxAnimationFormatRevision.CompressedV2,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV2Tag,
        9,
        9,
        16)]
    [InlineData(
        EnterElectroFinalBuild,
        "mj.psx",
        PsxMeshFormatRevision.NeversoftV4,
        PsxAnimLayoutVariant.PrototypeSparse,
        PsxAnimationFormatRevision.CompressedV2PrototypeSparse,
        PsxCharacterRuntimeRevision.ClassicSuper,
        PsxMeshFile.HierChunkV2Tag,
        2,
        1,
        18)]
    public void Parse_RepresentativePsxCharacterFiles_ClassifiesMeshAnimationAndRuntimeRevisions(
        string buildName,
        string fileName,
        PsxMeshFormatRevision expectedMeshRevision,
        PsxAnimLayoutVariant expectedLayout,
        PsxAnimationFormatRevision expectedAnimationRevision,
        PsxCharacterRuntimeRevision expectedRuntimeRevision,
        uint expectedChunkTag,
        int expectedDeclaredStreams,
        int expectedRecoveredEntries,
        int expectedBoneCount)
    {
        var path = paths.FindSampleFile(buildName, fileName);
        Assert.SkipWhen(path == null, $"{fileName} not found in {buildName}");

        var data = File.ReadAllBytes(path!);
        var psxFile = PsxMeshFile.Parse(data);
        Assert.NotNull(psxFile);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count);
        Assert.NotNull(animFile);

        Assert.Equal(expectedMeshRevision, psxFile.FormatRevision);
        Assert.Equal(expectedBoneCount, psxFile.Objects.Count);
        Assert.Equal(expectedLayout, animFile.Layout);
        Assert.Equal(expectedAnimationRevision, animFile.FormatRevision);
        Assert.Equal(expectedRuntimeRevision, animFile.MinimumRuntimeRevision);
        Assert.Equal(expectedChunkTag, animFile.ChunkTag);
        Assert.Equal(expectedDeclaredStreams, animFile.NumStreamsDeclared);
        Assert.Equal(expectedRecoveredEntries, animFile.Entries.Count);
        Assert.Equal(expectedRuntimeRevision == PsxCharacterRuntimeRevision.ExtendedAnimSlots,
            animFile.RequiresExtendedAnimationSlotIndex);
        Assert.Equal(expectedLayout == PsxAnimLayoutVariant.DirectMatrix, animFile.IsDirectMatrix);
    }
}
