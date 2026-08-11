using System.Numerics;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaAnimationBindingPlanTests(TestPaths paths)
{
    private const string ThawGcBuild =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const string Thug2Ps2Build =
        "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";

    [Fact]
    public void Create_NoExplicitSource_PreservesSameRigIndexBinding()
    {
        var target = BuildSkeleton([0x100u, 0x101u, 0x102u]);

        var plan = SkaAnimationBindingPlan.Create(target, null);

        Assert.Equal(3, plan.ExpectedTrackCount);
        Assert.Null(plan.BoneMap);
        Assert.True(plan.MatchesTrackCount(3));
        Assert.True(plan.MatchesTrackCount(null));
        Assert.False(plan.MatchesTrackCount(2));
    }

    [Fact]
    public void Create_ExplicitSource_UsesSourceCountAndExactMap()
    {
        var source = BuildSkeleton([0x100u, 0x101u, 0x102u], [-1, 0, 0]);
        var target = BuildSkeleton([0x100u, 0x101u], [-1, 0]);
        var rig = new SkaAnimationSourceRig("source.ske", source);

        var plan = SkaAnimationBindingPlan.Create(target, rig);

        Assert.Equal(3, plan.ExpectedTrackCount);
        Assert.Equal([0, 1, -1], plan.BoneMap!.ToArray());
    }

    [Fact]
    public void AnimationProbe_ReclassifiesWithoutTreatingUnknownAsMismatch()
    {
        var source = new MemoryAssetSource("clip.ska", []);
        var fifty = new AnimationProbe(source, "50", 1f, 50, true);
        var unknown = new AnimationProbe(source, "unknown", 1f, null, true);

        Assert.False(fifty.WithExpectedBoneCount(52).MatchesSkeleton);
        Assert.True(fifty.WithExpectedBoneCount(50).MatchesSkeleton);
        Assert.True(unknown.WithExpectedBoneCount(52).MatchesSkeleton);
    }

    [Fact]
    public void SkeletonAssetLoader_ParsesSupportedStandaloneLayouts()
    {
        var ps2 = SkaAnimationSourceRig.Load(
            new MemoryAssetSource("human.ske.ps2", BuildPs2SkeletonBytes()));
        var cross = SkaAnimationSourceRig.Load(
            new MemoryAssetSource("human.ske", BuildThps4SkeletonBytes()));
        var ngc = SkaAnimationSourceRig.Load(
            new MemoryAssetSource("human.ske.ngc", BuildThps4SkeletonBytes()));

        Assert.Single(ps2.Skeleton.Bones);
        Assert.Single(cross.Skeleton.Bones);
        Assert.Single(ngc.Skeleton.Bones);
        Assert.Equal("human.ske.ngc", ngc.SourceDisplayName);
    }

    [Fact]
    public void SkeletonAssetLoader_RejectsBroadPlatformSuffixWithoutSkeStem()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            SkaAnimationSourceRig.Load(
                new MemoryAssetSource("not-a-skeleton.ps2", BuildPs2SkeletonBytes())));

        Assert.Contains("not a supported skeleton", error.Message);
    }

    [Fact]
    public void OperationControlState_SupersedingOperationCannotStrandRigButtonsDisabled()
    {
        var active = AnimationPanelOperationControlState.Create(
            characterReady: true,
            isN64Character: false,
            isPs2SceneCharacter: true,
            targetSkeletonKnown: true,
            sourceRigSelected: true,
            operationActive: true);
        var restored = AnimationPanelOperationControlState.Create(
            characterReady: true,
            isN64Character: false,
            isPs2SceneCharacter: true,
            targetSkeletonKnown: true,
            sourceRigSelected: true,
            operationActive: false);

        Assert.Equal(new AnimationPanelOperationControlState(false, false, false, false), active);
        Assert.Equal(new AnimationPanelOperationControlState(true, true, true, true), restored);
    }

    [Fact]
    public void OperationControlState_N64PreservesExternalSourceAndRigGates()
    {
        var state = AnimationPanelOperationControlState.Create(
            characterReady: true,
            isN64Character: true,
            isPs2SceneCharacter: false,
            targetSkeletonKnown: true,
            sourceRigSelected: false,
            operationActive: false);

        Assert.Equal(new AnimationPanelOperationControlState(false, false, false, false), state);
    }

    [Fact]
    public void RealThps7ToThps6Plan_Uses52TracksAndMaps48()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var sourcePath = CorpusPath(ThawGcBuild,
            "worlds", "Global", "global_s.apk", "thps7_human.ske.ngc");
        var targetPath = CorpusPath(Thug2Ps2Build,
            "DATAP", "skeletons", "thps6_human.ske.ps2");
        var clipPath = CorpusPath(ThawGcBuild,
            "worlds", "worldzones", "z_mainmenu", "z_mainmenu.apk", "anims",
            "thps7_skaterselect", "pro_idle01.ska.ska.ngc");
        Assert.SkipWhen(!File.Exists(sourcePath) || !File.Exists(targetPath) || !File.Exists(clipPath),
            "THAW/THUG2 human binding fixtures are not available");

        var rig = SkaAnimationSourceRig.Load(new FileSystemAssetSource(sourcePath));
        var target = SkeletonAssetLoader.Load(new FileSystemAssetSource(targetPath));
        var plan = SkaAnimationBindingPlan.Create(target, rig);
        var animation = SkaFile.Parse(
            File.ReadAllBytes(clipPath), SkaCommand.FindCompressTable(clipPath));

        Assert.Equal(52, plan.ExpectedTrackCount);
        Assert.Equal(52, animation.BoneTracks.Length);
        Assert.True(plan.MatchesTrackCount(animation.BoneTracks.Length));
        Assert.Equal(48, plan.BoneMap!.MappedBoneCount);
        Assert.Equal([-1, -1, -1, -1],
            new[] { plan.BoneMap[15], plan.BoneMap[16], plan.BoneMap[27], plan.BoneMap[28] });
        Assert.Equal(16, plan.BoneMap[17]);
    }

    private string CorpusPath(string build, params string[] parts) =>
        Path.Combine([paths.SampleBuildsDir!, build, .. parts]);

    private static Ps2Skeleton BuildSkeleton(uint[] names, int[]? parents = null)
    {
        var bones = new Ps2Bone[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            var parentIndex = parents?[index] ?? (index == 0 ? -1 : index - 1);
            bones[index] = new Ps2Bone
            {
                NameChecksum = names[index],
                ParentChecksum = parentIndex < 0 ? 0 : names[parentIndex],
                FlipChecksum = names[index],
                ParentIndex = parentIndex,
                LocalRotation = Quaternion.Identity,
                LocalTranslation = Vector3.Zero,
                InverseBindMatrix = Matrix4x4.Identity
            };
        }

        return new Ps2Skeleton { Version = 2, Flags = 0, Bones = bones };
    }

    private static byte[] BuildPs2SkeletonBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2);
        writer.Write(0);
        writer.Write(1);
        writer.Write(0x100u);
        writer.Write(0u);
        writer.Write(0x100u);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        return stream.ToArray();
    }

    private static byte[] BuildThps4SkeletonBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x12345678u);
        writer.Write(1);
        writer.Write(0x100u);
        writer.Write(0u);
        writer.Write(0x100u);
        return stream.ToArray();
    }

    private sealed class MemoryAssetSource(string name, byte[] bytes) : AssetSource
    {
        public override string DisplayName => name;
        public override string EntryName => name;
        public override byte[] ReadBytes() => bytes;
        public override bool CompanionExists(string nameWithExtension) => false;
        public override byte[]? TryReadCompanion(string nameWithExtension) => null;
        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) => null;
    }
}
