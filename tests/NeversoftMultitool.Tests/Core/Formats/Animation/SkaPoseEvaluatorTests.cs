using System.Numerics;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Rendering;
using NeversoftMultitool.Tests.Helpers;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaPoseEvaluatorTests(TestPaths paths)
{
    private static readonly float[] SampleTimes = [0.10f, 0.25f, 0.50f, 0.90f];

    private string Thps4BuildDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)");

    private string Thps4SkeDir => Path.Combine(Thps4BuildDir, "SKE");
    private string Thps4SkaDir => Path.Combine(Thps4BuildDir, "SKA");
    private string Thps4SkinDir => Path.Combine(Thps4BuildDir, "SKIN");

    [Fact]
    public void Evaluate_UsesVersionSpecificFallbackSemantics()
    {
        var bindRotations = new[]
        {
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.30f, -0.10f, 0.15f)),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.40f, 0.25f, 0.05f)),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.20f, 0.35f, -0.30f)),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.15f, -0.20f, 0.45f))
        };
        var bindTranslations = new[]
        {
            new Vector3(1.0f, 2.0f, 3.0f),
            new Vector3(-2.0f, 0.5f, 4.0f),
            new Vector3(3.5f, -1.0f, 0.25f),
            new Vector3(-0.75f, 1.25f, -2.5f)
        };

        var animation = new SkaAnimation
        {
            Version = 1,
            Flags = 0,
            Duration = 1.0f,
            BoneTracks =
            [
                new SkaBoneTrack
                {
                    BoneIndex = 0,
                    RotationKeys = [],
                    TranslationKeys = []
                },
                new SkaBoneTrack
                {
                    BoneIndex = 1,
                    RotationKeys = [new SkaRotationKey(0f, Quaternion.Identity)],
                    TranslationKeys = [new SkaTranslationKey(0f, Vector3.Zero)]
                },
                new SkaBoneTrack
                {
                    BoneIndex = 2,
                    RotationKeys = [new SkaRotationKey(0f,
                        Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.75f, -0.25f, 0.10f)))],
                    TranslationKeys = [new SkaTranslationKey(0f, new Vector3(6.0f, -3.0f, 2.5f))]
                },
                new SkaBoneTrack
                {
                    BoneIndex = 3,
                    RotationKeys =
                    [
                        new SkaRotationKey(0f,
                            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.20f, 0.05f, -0.10f))),
                        new SkaRotationKey(1f,
                            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.50f, 0.30f, 0.25f)))
                    ],
                    TranslationKeys =
                    [
                        new SkaTranslationKey(0f, new Vector3(-1.0f, 2.0f, 0.5f)),
                        new SkaTranslationKey(1f, new Vector3(5.0f, -2.0f, 3.5f))
                    ]
                }
            ]
        };

        var thps4Poses = new SkaPoseEvaluator(animation, CreateSkeleton(1, bindRotations, bindTranslations))
            .Evaluate(0.25f);
        var thugPoses = new SkaPoseEvaluator(animation, CreateSkeleton(2, bindRotations, bindTranslations))
            .Evaluate(0.25f);

        AssertVectorClose(Vector3.Zero, thps4Poses[0].Translation);
        AssertQuaternionClose(Quaternion.Identity, thps4Poses[0].Rotation);
        AssertVectorClose(bindTranslations[0], thugPoses[0].Translation);
        AssertQuaternionClose(bindRotations[0], thugPoses[0].Rotation);

        AssertVectorClose(Vector3.Zero, thps4Poses[1].Translation);
        AssertQuaternionClose(Quaternion.Identity, thps4Poses[1].Rotation);
        AssertVectorClose(bindTranslations[1], thugPoses[1].Translation);
        AssertQuaternionClose(bindRotations[1], thugPoses[1].Rotation);

        var constantRotation = animation.BoneTracks[2].RotationKeys[0].Rotation;
        var constantTranslation = animation.BoneTracks[2].TranslationKeys[0].Translation;
        AssertVectorClose(constantTranslation, thps4Poses[2].Translation);
        AssertQuaternionClose(constantRotation, thps4Poses[2].Rotation);
        AssertVectorClose(constantTranslation, thugPoses[2].Translation);
        AssertQuaternionClose(constantRotation, thugPoses[2].Rotation);

        var expectedAnimatedRotation = Quaternion.Normalize(Quaternion.Slerp(
            animation.BoneTracks[3].RotationKeys[0].Rotation,
            animation.BoneTracks[3].RotationKeys[1].Rotation,
            0.25f));
        var expectedAnimatedTranslation = Vector3.Lerp(
            animation.BoneTracks[3].TranslationKeys[0].Translation,
            animation.BoneTracks[3].TranslationKeys[1].Translation,
            0.25f);
        AssertVectorClose(expectedAnimatedTranslation, thps4Poses[3].Translation);
        AssertQuaternionClose(expectedAnimatedRotation, thps4Poses[3].Rotation);
        AssertVectorClose(expectedAnimatedTranslation, thugPoses[3].Translation);
        AssertQuaternionClose(expectedAnimatedRotation, thugPoses[3].Rotation);
    }

    [Fact]
    public void Evaluate_WrapsLoopingTime()
    {
        var animation = new SkaAnimation
        {
            Version = 1,
            Flags = 0,
            Duration = 1.0f,
            BoneTracks =
            [
                new SkaBoneTrack
                {
                    BoneIndex = 0,
                    RotationKeys =
                    [
                        new SkaRotationKey(0f,
                            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0f, 0f, 0f))),
                        new SkaRotationKey(1f,
                            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.8f, -0.3f, 0.2f)))
                    ],
                    TranslationKeys =
                    [
                        new SkaTranslationKey(0f, Vector3.Zero),
                        new SkaTranslationKey(1f, new Vector3(8f, -4f, 2f))
                    ]
                }
            ]
        };

        var evaluator = new SkaPoseEvaluator(animation, CreateSkeleton(1,
            [Quaternion.Identity],
            [Vector3.Zero]));

        var wrapped = evaluator.Evaluate(0.25f)[0];
        var looped = evaluator.Evaluate(1.25f)[0];

        AssertMatrixClose(wrapped.WorldMatrix, looped.WorldMatrix, 1e-6f);
    }

    [Fact]
    public void ApplyAnimation_Version1SkipsConstantTracks_Version2PreservesThem()
    {
        var constantRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.3f));
        var constantTranslation = new Vector3(3f, -2f, 1f);
        var animation = new SkaAnimation
        {
            Version = 1,
            Flags = 0,
            Duration = 1f,
            BoneTracks =
            [
                new SkaBoneTrack
                {
                    BoneIndex = 0,
                    RotationKeys = [new SkaRotationKey(0f, constantRotation)],
                    TranslationKeys = [new SkaTranslationKey(0f, constantTranslation)]
                }
            ]
        };

        var version1Skeleton = CreateSkeleton(1, [Quaternion.Identity], [Vector3.Zero]);
        var version2Skeleton = CreateSkeleton(2, [Quaternion.Identity], [Vector3.Zero]);

        var version1Channels = SkaGltfWriter.ApplyAnimation(
            SkaGltfWriter.BuildJointHierarchy(version1Skeleton),
            version1Skeleton,
            animation,
            "anim");
        var version2Channels = SkaGltfWriter.ApplyAnimation(
            SkaGltfWriter.BuildJointHierarchy(version2Skeleton),
            version2Skeleton,
            animation,
            "anim");

        Assert.Equal(0, version1Channels);
        Assert.Equal(2, version2Channels);
    }

    [Fact]
    public void BuildJointHierarchy_Version1SuppressesOnlyUnresolvedConstantRestRotations()
    {
        var resolvedConstantRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.25f, 0.15f, 0.05f));
        var constantRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.35f, -0.20f, 0.10f));
        var animation = new SkaAnimation
        {
            Version = 1,
            Flags = 0,
            Duration = 1f,
            BoneTracks =
            [
                new SkaBoneTrack
                {
                    BoneIndex = 0,
                    RotationKeys = [new SkaRotationKey(0f, resolvedConstantRotation)],
                    TranslationKeys = [new SkaTranslationKey(0f, new Vector3(4f, 5f, 6f))]
                },
                new SkaBoneTrack
                {
                    BoneIndex = 1,
                    RotationKeys = [new SkaRotationKey(0f, constantRotation)],
                    TranslationKeys = [new SkaTranslationKey(0f, new Vector3(1f, 2f, 3f))]
                }
            ]
        };

        var skeleton = new Ps2Skeleton
        {
            Version = 1,
            Flags = 0,
            Bones =
            [
                new Ps2Bone
                {
                    NameChecksum = NeversoftMultitool.Core.QbKey.QbKey.HashLower("root"),
                    ParentChecksum = 0,
                    FlipChecksum = 0,
                    ParentIndex = -1,
                    LocalRotation = Quaternion.Identity,
                    LocalTranslation = Vector3.Zero,
                    InverseBindMatrix = Matrix4x4.Identity
                },
                new Ps2Bone
                {
                    NameChecksum = 0x1001,
                    ParentChecksum = 0x1000,
                    FlipChecksum = 0,
                    ParentIndex = 0,
                    LocalRotation = Quaternion.Identity,
                    LocalTranslation = Vector3.Zero,
                    InverseBindMatrix = Matrix4x4.Identity
                }
            ]
        };

        var joints = SkaGltfWriter.BuildJointHierarchy(skeleton, animation);

        AssertQuaternionClose(resolvedConstantRotation, joints[0].LocalTransform.Rotation);
        AssertVectorClose(new Vector3(4f, 5f, 6f), joints[0].LocalTransform.Translation);
        AssertQuaternionClose(Quaternion.Identity, joints[1].LocalTransform.Rotation);
        AssertVectorClose(new Vector3(1f, 2f, 3f), joints[1].LocalTransform.Translation);
    }

    [Fact]
    public void ParseAndExport_PedFWalkFactsStayStable()
    {
        var fixture = LoadThps4Fixture("Ped_F.ske", "skater_f.skin.ps2", "Ped_F_Walk.ska.ps2");

        Assert.Equal(0x06800000u, fixture.Animation.Flags);
        Assert.True(fixture.Animation.IsCompressedTime);
        Assert.True(fixture.Animation.IsPreRotatedRoot);
        Assert.True(fixture.Animation.UsesCompressTable);

        var gltfAnimation = fixture.Model.LogicalAnimations.Single();
        Assert.Equal(28, gltfAnimation.Channels.Count(channel => channel.TargetNodePath == PropertyPath.rotation));
        Assert.Equal(2, gltfAnimation.Channels.Count(channel => channel.TargetNodePath == PropertyPath.translation));
        Assert.Equal(0, gltfAnimation.Channels.Count(channel => channel.TargetNodePath == PropertyPath.scale));
    }

    [Fact]
    public void BuildSkinnedAnimated_Thps4SeedsRestPoseFromInitialAnimationSample()
    {
        var fixture = LoadThps4Fixture("Ped_F.ske", "skater_f.skin.ps2", "Ped_F_Walk.ska.ps2");
        var initialPose = new SkaPoseEvaluator(fixture.Animation, fixture.Skeleton).Evaluate(0f);
        var skin = fixture.Model.LogicalSkins.Single();

        foreach (var boneIndex in GetTrackedBoneIndices(fixture.Skeleton))
        {
            var joint = skin.GetJoint(boneIndex).Item1;
            AssertVectorClose(initialPose[boneIndex].Translation, joint.LocalTransform.Translation);
            AssertQuaternionClose(initialPose[boneIndex].Rotation, joint.LocalTransform.Rotation);
        }
    }

    [Fact]
    public void BuildSkinnedAnimated_Version2PreservesExplicitInverseBindMatrices()
    {
        var explicitInverseBind = Matrix4x4.CreateTranslation(-7f, 3f, 11f);
        var skeleton = new Ps2Skeleton
        {
            Version = 2,
            Flags = 0,
            Bones =
            [
                new Ps2Bone
                {
                    NameChecksum = 0x1000,
                    ParentChecksum = 0,
                    FlipChecksum = 0,
                    ParentIndex = -1,
                    LocalRotation = Quaternion.Identity,
                    LocalTranslation = new Vector3(4f, 5f, 6f),
                    InverseBindMatrix = explicitInverseBind
                }
            ]
        };

        var scene = CreateSingleBoneScene();
        var animation = new SkaAnimation
        {
            Version = 2,
            Flags = 0,
            Duration = 1f,
            BoneTracks =
            [
                new SkaBoneTrack
                {
                    BoneIndex = 0,
                    RotationKeys = [new SkaRotationKey(0f, Quaternion.Identity)],
                    TranslationKeys = [new SkaTranslationKey(0f, new Vector3(4f, 5f, 6f))]
                }
            ]
        };

        var (model, triangles) = Ps2SceneGltfWriter.BuildSkinnedAnimated(scene, skeleton, animation, "anim");

        Assert.True(triangles > 0);
        var skin = model.LogicalSkins.Single();
        Assert.Equal(1, skin.JointsCount);
        AssertMatrixClose(explicitInverseBind, skin.GetJoint(0).Item2, 1e-5f);
    }

    [Theory]
    [InlineData("Ped_F.ske", "skater_f.skin.ps2", "Ped_F_Walk.ska.ps2")]
    [InlineData("human.ske", "skater_m.skin.ps2", "AirIdle.ska.ps2")]
    [InlineData("human.ske", "skater_m.skin.ps2", "AirWalk.ska.ps2")]
    public void Evaluate_Thps4Fixtures_SelectedBoneWorldPosesStayFinite(
        string skeletonFileName,
        string skinFileName,
        string animationFileName)
    {
        var fixture = LoadThps4Fixture(skeletonFileName, skinFileName, animationFileName);

        var evaluator = new SkaPoseEvaluator(fixture.Animation, fixture.Skeleton);

        foreach (var time in GetSampleTimes(fixture.Animation))
        {
            var poses = evaluator.Evaluate(time);
            foreach (var boneIndex in GetTrackedBoneIndices(fixture.Skeleton))
            {
                AssertMatrixIsFinite(poses[boneIndex].WorldMatrix,
                    $"{animationFileName} bone {GetBoneLabel(fixture.Skeleton, boneIndex)} at t={time:F2}");
            }
        }
    }

    [Fact]
    public void GlbModelLoader_PedFWalkBuiltInAndCustomSamplingStayInParity()
    {
        var fixture = LoadThps4Fixture("Ped_F.ske", "skater_f.skin.ps2", "Ped_F_Walk.ska.ps2");
        Assert.True(fixture.Animation.Duration > SampleTimes[^1],
            $"Ped_F_Walk duration {fixture.Animation.Duration:F3}s is shorter than the regression sample window.");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_PedFWalk_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var glbPath = Path.Combine(tempDir, "Ped_F_Walk.glb");
            fixture.Model.SaveGLB(glbPath);

            var loaded = ModelRoot.Load(glbPath);
            var gltfAnimation = loaded.LogicalAnimations.Single();

            foreach (var time in SampleTimes)
            {
                foreach (var node in loaded.LogicalNodes)
                {
                    var builtIn = node.GetWorldMatrix(gltfAnimation, time);
                    var custom = GlbModelLoader.EvaluateAnimatedWorldMatrixForTesting(node, gltfAnimation, time);
                    AssertMatrixClose(builtIn, custom, 2e-5f,
                        $"{node.Name ?? $"node_{node.LogicalIndex}"} at t={time:F2}");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private Thps4Fixture LoadThps4Fixture(
        string skeletonFileName,
        string skinFileName,
        string animationFileName)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var skeletonPath = Path.Combine(Thps4SkeDir, skeletonFileName);
        var skinPath = Path.Combine(Thps4SkinDir, skinFileName);
        var animationPath = Path.Combine(Thps4SkaDir, animationFileName);

        Assert.SkipWhen(!File.Exists(skeletonPath), $"Test file not found: {skeletonFileName}");
        Assert.SkipWhen(!File.Exists(skinPath), $"Test file not found: {skinFileName}");
        Assert.SkipWhen(!File.Exists(animationPath), $"Test file not found: {animationFileName}");

        var skeleton = SkeletonFile.Parse(skeletonPath);
        var skin = Ps2SceneFile.Parse(skinPath);
        var compressTable = SkaCommand.FindCompressTable(animationPath);
        Assert.NotNull(compressTable);

        var animation = SkaFile.Parse(File.ReadAllBytes(animationPath), compressTable);
        var animationName = Path.GetFileNameWithoutExtension(
            Path.GetFileNameWithoutExtension(animationFileName));
        var (model, triangles) = Ps2SceneGltfWriter.BuildSkinnedAnimated(
            skin, skeleton, animation, animationName);

        Assert.True(triangles > 0, $"{animationFileName}: expected a non-empty skinned export.");
        Assert.Single(model.LogicalAnimations);
        Assert.Single(model.LogicalSkins);

        return new Thps4Fixture(skeleton, animation, model);
    }

    private static float[] GetSampleTimes(SkaAnimation animation)
    {
        var times = SampleTimes
            .Where(time => time < animation.Duration)
            .ToArray();
        Assert.NotEmpty(times);
        return times;
    }

    private static Ps2Skeleton CreateSkeleton(
        int version,
        Quaternion[] bindRotations,
        Vector3[] bindTranslations)
    {
        var bones = new Ps2Bone[bindRotations.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            bones[i] = new Ps2Bone
            {
                NameChecksum = (uint)(0x1000 + i),
                ParentChecksum = 0,
                FlipChecksum = 0,
                ParentIndex = -1,
                LocalRotation = bindRotations[i],
                LocalTranslation = bindTranslations[i],
                InverseBindMatrix = Matrix4x4.Identity
            };
        }

        return new Ps2Skeleton
        {
            Version = version,
            Flags = 0,
            Bones = bones
        };
    }

    private static Ps2Scene CreateSingleBoneScene()
    {
        return new Ps2Scene
        {
            MaterialVersion = 3,
            MeshVersion = 4,
            VertexVersion = 1,
            Materials =
            [
                new Ps2Material
                {
                    Checksum = 0x2000,
                    Flags = 0,
                    TextureChecksum = 0,
                    GroupChecksum = 0,
                    AlphaRef = 0,
                    ClampUMode = 0,
                    ClampVMode = 0,
                    RegAlpha = 0
                }
            ],
            MeshGroups =
            [
                new Ps2MeshGroup
                {
                    Checksum = 0x3000,
                    Meshes =
                    [
                        new Ps2Mesh
                        {
                            Checksum = 0x4000,
                            MaterialChecksum = 0x2000,
                            MeshFlags = 0,
                            BoundingSphere = Vector4.Zero,
                            StartsOnOddOutputSlot = false,
                            Vertices =
                            [
                                new Ps2Vertex(
                                    new Vector3(0f, 0f, 0f), Vector3.UnitY,
                                    128, 128, 128, 128,
                                    0f, 0f,
                                    true, true, false, false,
                                    0, 0, 0,
                                    1f, 0f, 0f,
                                    true),
                                new Ps2Vertex(
                                    new Vector3(1f, 0f, 0f), Vector3.UnitY,
                                    128, 128, 128, 128,
                                    0f, 0f,
                                    true, true, false, false,
                                    0, 0, 0,
                                    1f, 0f, 0f,
                                    true),
                                new Ps2Vertex(
                                    new Vector3(0f, 1f, 0f), Vector3.UnitY,
                                    128, 128, 128, 128,
                                    0f, 0f,
                                    true, true, false, false,
                                    0, 0, 0,
                                    1f, 0f, 0f,
                                    true)
                            ]
                        }
                    ]
                }
            ]
        };
    }

    private static int[] GetTrackedBoneIndices(Ps2Skeleton skeleton)
    {
        var nameToIndex = skeleton.Bones
            .Select((bone, index) => new
            {
                Name = NeversoftMultitool.Core.QbKey.QbKey.TryResolve(bone.NameChecksum),
                Index = index
            })
            .Where(entry => entry.Name != null)
            .ToDictionary(entry => entry.Name!, entry => entry.Index, StringComparer.OrdinalIgnoreCase);

        var tracked = new List<int> { 0 };
        foreach (var boneName in new[]
                 {
                     "neck",
                     "head",
                     "left_bicep",
                     "left_forearm",
                     "right_bicep",
                     "right_forearm"
                 })
        {
            Assert.True(nameToIndex.TryGetValue(boneName, out var boneIndex),
                $"Expected skeleton to resolve bone '{boneName}'.");
            tracked.Add(boneIndex);
        }

        return tracked.ToArray();
    }

    private static string GetBoneLabel(Ps2Skeleton skeleton, int boneIndex)
    {
        if (boneIndex == 0)
            return "root";

        return NeversoftMultitool.Core.QbKey.QbKey.TryResolve(skeleton.Bones[boneIndex].NameChecksum)
               ?? $"bone_{boneIndex}";
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual, float tolerance = 1e-5f)
    {
        var delta = Vector3.Distance(expected, actual);
        Assert.True(delta <= tolerance,
            $"Expected {expected}, got {actual} (delta {delta}).");
    }

    private static void AssertQuaternionClose(Quaternion expected, Quaternion actual, float tolerance = 1e-5f)
    {
        var dot = MathF.Abs(Quaternion.Dot(
            Quaternion.Normalize(expected),
            Quaternion.Normalize(actual)));
        Assert.True(1f - dot <= tolerance,
            $"Expected {expected}, got {actual} (|dot| {dot}).");
    }

    private static void AssertMatrixClose(
        Matrix4x4 expected,
        Matrix4x4 actual,
        float tolerance,
        string? context = null)
    {
        var diff = GetMaxMatrixDiff(expected, actual);
        Assert.True(diff <= tolerance,
            $"{context ?? "matrix"} diff {diff} exceeded tolerance {tolerance}.");
    }

    private static void AssertMatrixIsFinite(Matrix4x4 matrix, string context)
    {
        foreach (var value in new[]
                 {
                     matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                     matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                     matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                     matrix.M41, matrix.M42, matrix.M43, matrix.M44
                 })
        {
            Assert.True(float.IsFinite(value), $"{context} contained non-finite matrix data.");
        }
    }

    private static float GetMaxMatrixDiff(Matrix4x4 left, Matrix4x4 right)
    {
        return Math.Max(
            Math.Max(
                Math.Max(MathF.Abs(left.M11 - right.M11), MathF.Abs(left.M12 - right.M12)),
                Math.Max(MathF.Abs(left.M13 - right.M13), MathF.Abs(left.M14 - right.M14))),
            Math.Max(
                Math.Max(
                    Math.Max(MathF.Abs(left.M21 - right.M21), MathF.Abs(left.M22 - right.M22)),
                    Math.Max(MathF.Abs(left.M23 - right.M23), MathF.Abs(left.M24 - right.M24))),
                Math.Max(
                    Math.Max(MathF.Abs(left.M31 - right.M31), MathF.Abs(left.M32 - right.M32)),
                    Math.Max(
                        Math.Max(MathF.Abs(left.M33 - right.M33), MathF.Abs(left.M34 - right.M34)),
                        Math.Max(
                            Math.Max(MathF.Abs(left.M41 - right.M41), MathF.Abs(left.M42 - right.M42)),
                            Math.Max(MathF.Abs(left.M43 - right.M43), MathF.Abs(left.M44 - right.M44)))))));
    }

    private sealed record Thps4Fixture(
        Ps2Skeleton Skeleton,
        SkaAnimation Animation,
        ModelRoot Model);
}
