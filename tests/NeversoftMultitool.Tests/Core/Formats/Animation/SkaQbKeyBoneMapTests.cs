using System.Numerics;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public class SkaQbKeyBoneMapTests(TestPaths paths)
{
    private const string ThawGcBuild =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const string Thug2Ps2Build =
        "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";
    private static readonly int[] LegacySkippedSourceBones = [15, 16, 27, 28];

    [Fact]
    public void Create_IdentitySkeleton_ProducesIdentityMap()
    {
        var skeleton = BuildSkeleton([0x100u, 0x101u, 0x102u]);

        var map = SkaQbKeyBoneMap.Create(skeleton, skeleton);

        Assert.Equal(3, map.MappedBoneCount);
        Assert.Equal([0, 1, 2], map.ToArray());
    }

    [Fact]
    public void Create_Thps7ToThps6Shape_Maps48SkipsFourAndLeavesTwoTargetBonesAtBind()
    {
        var (source, target) = BuildLegacyHumanSkeletons();

        var map = SkaQbKeyBoneMap.Create(source, target);

        Assert.Equal(52, map.SourceBoneCount);
        Assert.Equal(48, map.MappedBoneCount);
        Assert.Equal(-1, map[15]);
        Assert.Equal(-1, map[16]);
        Assert.Equal(16, map[17]);
        Assert.Equal(-1, map[27]);
        Assert.Equal(-1, map[28]);
        Assert.DoesNotContain(15, map);
        Assert.DoesNotContain(26, map);

        var animation = BuildTranslationAnimation(52);
        var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
            target, [("legacy", animation)], "legacy", map);
        var channels = Assert.Single(document.Animations).Channels;

        Assert.Equal(48, channels.Count);
        Assert.DoesNotContain(channels, static channel => channel.BoneIndex is 15 or 26);
        var remapped = Assert.Single(channels, static channel => channel.BoneIndex == 16);
        Assert.Equal(18f, remapped.Values[0]);
    }

    [Fact]
    public void Create_SameCountButRenamedRoot_RejectsInsteadOfInferringByIndex()
    {
        var source = BuildSkeleton([0x100u, 0x101u, 0x102u]);
        var target = BuildSkeleton([0x900u, 0x101u, 0x102u]);

        var error = Assert.Throws<InvalidDataException>(
            () => SkaQbKeyBoneMap.Create(source, target));

        Assert.Contains("root QbKey 0x00000100", error.Message);
    }

    [Fact]
    public void Create_DuplicateQbKeysInEitherSkeleton_RejectsAmbiguousMap()
    {
        var valid = BuildSkeleton([0x100u, 0x101u]);
        var duplicate = BuildSkeleton([0x100u, 0x100u]);

        Assert.Contains("duplicate QbKey", Assert.Throws<InvalidDataException>(
            () => SkaQbKeyBoneMap.Create(duplicate, valid)).Message);
        Assert.Contains("duplicate QbKey", Assert.Throws<InvalidDataException>(
            () => SkaQbKeyBoneMap.Create(valid, duplicate)).Message);
    }

    [Fact]
    public void Create_MalformedHierarchy_RejectsCycle()
    {
        var malformed = BuildSkeleton([0x100u, 0x101u], [1, 0]);
        var target = BuildSkeleton([0x100u, 0x101u]);

        var error = Assert.Throws<InvalidDataException>(
            () => SkaQbKeyBoneMap.Create(malformed, target));

        Assert.Contains("cycle", error.Message);
    }

    [Fact]
    public void Create_TargetInsertsParentBetweenMappedBones_RejectsChangedLocalBasis()
    {
        var source = BuildSkeleton([0x100u, 0x101u], [-1, 0]);
        var target = BuildSkeleton([0x100u, 0x900u, 0x101u], [-1, 0, 1]);

        var error = Assert.Throws<InvalidDataException>(
            () => SkaQbKeyBoneMap.Create(source, target));

        Assert.Contains("changes parent edge", error.Message);
        Assert.Contains("0x00000101", error.Message);
    }

    [Fact]
    public void Create_MappedChildBelowSourceOnlyParent_RejectsMissingLocalBasis()
    {
        var source = BuildSkeleton([0x100u, 0x900u, 0x101u], [-1, 0, 1]);
        var target = BuildSkeleton([0x100u, 0x101u], [-1, 0]);

        var error = Assert.Throws<InvalidDataException>(
            () => SkaQbKeyBoneMap.Create(source, target));

        Assert.Contains("unmapped source parent", error.Message);
        Assert.Contains("0x00000900", error.Message);
    }

    [Fact]
    public void Writer_TrackCountMismatch_RejectsValidatedMap()
    {
        var source = BuildSkeleton([0x100u, 0x101u]);
        var target = BuildSkeleton([0x100u, 0x101u]);
        var map = SkaQbKeyBoneMap.Create(source, target);

        var error = Assert.Throws<InvalidDataException>(() =>
            SkaModelDocumentBuilder.BuildSkeletonOnly(
                target, [("bad", BuildTranslationAnimation(1))], "bad", map));

        Assert.Contains("1 tracks", error.Message);
        Assert.Contains("2 bones", error.Message);
    }

    [Fact]
    public void Cli_AnimationSkeletonWithoutTargetSkeleton_IsRejected()
    {
        var exitCode = SkaCommand.Execute(
            "missing.ska", "unused", false, null, null, null, null, "source.ske");

        Assert.Equal(1, exitCode);
    }

    [CorpusFact]
    public void RealThps7HumanToThps6Human_MapAndClipMatchProvenLegacyShape()
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

        var source = SkaCommand.LoadSkeleton(sourcePath);
        var target = SkaCommand.LoadSkeleton(targetPath);
        var map = SkaQbKeyBoneMap.Create(source, target);
        var animation = SkaFile.Parse(
            File.ReadAllBytes(clipPath), SkaCommand.FindCompressTable(clipPath));

        Assert.Equal(52, source.Bones.Length);
        Assert.Equal(50, target.Bones.Length);
        Assert.Equal(source.Bones.Length, animation.BoneTracks.Length);
        Assert.Equal(48, map.MappedBoneCount);
        Assert.Equal(LegacySkippedSourceBones,
            Enumerable.Range(0, map.Count).Where(index => map[index] < 0));
        Assert.Equal(16, map[17]);
        Assert.DoesNotContain(15, map);
        Assert.DoesNotContain(26, map);

        var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
            target, [("pro_idle01", animation)], "thps6_human", map);
        var channels = Assert.Single(document.Animations).Channels;
        Assert.DoesNotContain(channels, static channel => channel.BoneIndex is 15 or 26);
        Assert.All(channels, static channel => Assert.InRange(channel.BoneIndex, 0, 49));
    }

    [CorpusFact]
    public void RealSameCountControlSkeletonWithRenamedRoot_IsRejected()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var sourcePath = CorpusPath(ThawGcBuild,
            "worlds", "Global", "global_s.apk", "thps7_human.ske.ngc");
        var controlPath = CorpusPath(ThawGcBuild,
            "cutscenes", "HO_3", "ngc", "ho_3_main", "ho_3_main.apk",
            "Control_Root_CAS_THPS7_human.ske.ngc");
        Assert.SkipWhen(!File.Exists(sourcePath) || !File.Exists(controlPath),
            "THAW control-skeleton fixtures are not available");

        var source = SkaCommand.LoadSkeleton(sourcePath);
        var control = SkaCommand.LoadSkeleton(controlPath);
        Assert.Equal(source.Bones.Length, control.Bones.Length);
        Assert.NotEqual(source.Bones[0].NameChecksum, control.Bones[0].NameChecksum);

        Assert.Throws<InvalidDataException>(() => SkaQbKeyBoneMap.Create(source, control));
    }

    [CorpusFact]
    public void RealCli_SourceSkeletonOption_ExportsMappedSkeletonOnlyGlb()
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
            "THAW/THUG2 CLI binding fixtures are not available");

        var temp = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_ThawQbBind_" + Guid.NewGuid().ToString("N"));
        try
        {
            var exitCode = SkaCommand.Execute(
                clipPath, temp, false, targetPath, null, null, null, sourcePath);
            Assert.Equal(0, exitCode);

            var glbPath = Path.Combine(temp, SkaCommand.GetOutputStem(clipPath) + ".glb");
            Assert.True(File.Exists(glbPath));
            var glb = ModelRoot.Load(glbPath);
            var animation = Assert.Single(glb.LogicalAnimations);
            Assert.NotEmpty(animation.Channels);

            var target = SkaCommand.LoadSkeleton(targetPath);
            var targetModel = Ps2SceneGeometryWriter.BuildPs2Skeleton(target);
            var targetOnlyNames = new[]
            {
                targetModel.Bones[15].Name,
                targetModel.Bones[26].Name
            };
            Assert.DoesNotContain(animation.Channels,
                channel => targetOnlyNames.Contains(channel.TargetNode.Name, StringComparer.Ordinal));

            var mappedNames = SkaQbKeyBoneMap.Create(
                    SkaCommand.LoadSkeleton(sourcePath), target)
                .Where(static index => index >= 0)
                .Select(index => targetModel.Bones[index].Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(animation.Channels,
                channel => Assert.Contains(channel.TargetNode.Name, mappedNames));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }

    [CorpusFact]
    public void RealCli_SourceSkeletonOption_ExportsMappedTargetAuthoredThug2Iskin()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var sourcePath = CorpusPath(ThawGcBuild,
            "worlds", "Global", "global_s.apk", "thps7_human.ske.ngc");
        var targetPath = CorpusPath(Thug2Ps2Build,
            "DATAP", "skeletons", "thps6_human.ske.ps2");
        var clipPath = CorpusPath(ThawGcBuild,
            "worlds", "worldzones", "z_mainmenu", "z_mainmenu.apk", "anims",
            "thps7_skaterselect", "pro_idle01.ska.ska.ngc");
        var skinPath = CorpusPath(Thug2Ps2Build,
            "DATAP", "models", "peds", "ped_skater_ny1", "ped_skater_ny1.iskin.ps2");
        Assert.SkipWhen(!File.Exists(sourcePath) || !File.Exists(targetPath) ||
                        !File.Exists(clipPath) || !File.Exists(skinPath),
            "THAW/THUG2 combined binding fixtures are not available");

        // This pins the ordinary PS2-scene branch with a target-authored THUG2
        // .iskin.ps2. Native THAW .skin.ps2 routing is a separate format path
        // and is intentionally not claimed by this QbKey binding slice.
        var temp = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_ThawQbBindSkin_" + Guid.NewGuid().ToString("N"));
        try
        {
            var exitCode = SkaCommand.Execute(
                clipPath, temp, false, targetPath, skinPath, null, null, sourcePath);
            Assert.Equal(0, exitCode);

            var glbPath = Path.Combine(temp, SkaCommand.GetOutputStem(clipPath) + ".glb");
            Assert.True(File.Exists(glbPath));
            var glb = ModelRoot.Load(glbPath);
            var mesh = Assert.Single(glb.LogicalMeshes);
            Assert.Equal(8, mesh.Primitives.Count);
            Assert.Equal(1249, mesh.Primitives.Sum(static primitive =>
                primitive.IndexAccessor!.AsIndicesArray().Count / 3));
            Assert.All(mesh.Primitives, static primitive =>
            {
                Assert.Contains("JOINTS_0", primitive.VertexAccessors.Keys);
                Assert.Contains("WEIGHTS_0", primitive.VertexAccessors.Keys);
            });

            var skin = Assert.Single(glb.LogicalSkins);
            Assert.Equal(50, skin.JointsCount);
            Assert.Single(glb.LogicalNodes, static node => node.Skin != null);

            var animation = Assert.Single(glb.LogicalAnimations);
            Assert.Equal(35, animation.Channels.Count);
            Assert.Equal(34, animation.Channels
                .Select(static channel => channel.TargetNode)
                .Distinct()
                .Count());

            var target = SkaCommand.LoadSkeleton(targetPath);
            var targetModel = Ps2SceneGeometryWriter.BuildPs2Skeleton(target);
            var targetOnlyNames = new[]
            {
                targetModel.Bones[15].Name,
                targetModel.Bones[26].Name
            };
            Assert.DoesNotContain(animation.Channels,
                channel => targetOnlyNames.Contains(channel.TargetNode.Name, StringComparer.Ordinal));

            var mappedNames = SkaQbKeyBoneMap.Create(
                    SkaCommand.LoadSkeleton(sourcePath), target)
                .Where(static index => index >= 0)
                .Select(index => targetModel.Bones[index].Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(animation.Channels,
                channel => Assert.Contains(channel.TargetNode.Name, mappedNames));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }

    private string CorpusPath(string build, params string[] parts) =>
        Path.Combine([paths.SampleBuildsDir!, build, .. parts]);

    private static (Ps2Skeleton Source, Ps2Skeleton Target) BuildLegacyHumanSkeletons()
    {
        var sourceNames = new uint[52];
        var targetNames = new uint[50];
        for (var i = 0; i < 15; i++)
            sourceNames[i] = targetNames[i] = 0x1000u + (uint)i;

        sourceNames[15] = 0xE015;
        sourceNames[16] = 0xE016;
        targetNames[15] = 0xF015;
        for (var source = 17; source <= 26; source++)
            sourceNames[source] = targetNames[source - 1] = 0x1000u + (uint)source;

        sourceNames[27] = 0xE027;
        sourceNames[28] = 0xE028;
        targetNames[26] = 0xF026;
        for (var source = 29; source < sourceNames.Length; source++)
            sourceNames[source] = targetNames[source - 2] = 0x1000u + (uint)source;

        return (BuildSkeleton(sourceNames), BuildSkeleton(targetNames));
    }

    private static Ps2Skeleton BuildSkeleton(uint[] names, int[]? parents = null)
    {
        parents ??= Enumerable.Range(0, names.Length)
            .Select(static index => index == 0 ? -1 : 0)
            .ToArray();
        var bones = new Ps2Bone[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            var parent = parents[index];
            bones[index] = new Ps2Bone
            {
                NameChecksum = names[index],
                ParentChecksum = parent >= 0 ? names[parent] : 0,
                FlipChecksum = 0,
                ParentIndex = parent,
                LocalRotation = Quaternion.Identity,
                LocalTranslation = Vector3.Zero,
                InverseBindMatrix = Matrix4x4.Identity
            };
        }

        return new Ps2Skeleton { Version = 2, Flags = 0, Bones = bones };
    }

    private static SkaAnimation BuildTranslationAnimation(int boneCount)
    {
        return new SkaAnimation
        {
            Version = 0x28,
            Flags = 0,
            Duration = 1f,
            BoneTracks = Enumerable.Range(0, boneCount)
                .Select(static index => new SkaBoneTrack
                {
                    BoneIndex = index,
                    RotationKeys = [],
                    TranslationKeys =
                    [
                        new SkaTranslationKey(0f, new Vector3(index + 1f, 0f, 0f))
                    ]
                })
                .ToArray()
        };
    }
}
