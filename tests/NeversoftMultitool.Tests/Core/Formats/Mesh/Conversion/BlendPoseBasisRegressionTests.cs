using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Headless-Blender regression for converting absolute ModelDocument/glTF
///     animation TRS channels into Blender pose-bone matrix_basis channels.
///     The synthetic non-PSX rig deliberately has rotated and translated bind
///     transforms plus multi-bone vertex weights; the old PSX-only translation
///     bias moved this mesh even when an animation reproduced its bind pose.
/// </summary>
public sealed class BlendPoseBasisRegressionTests(TestPaths paths)
{
    private const string Thps4Build = "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)";
    private static readonly Vector3 RootBindTranslation = new(3.5f, -2.0f, 4.25f);
    private static readonly Quaternion RootBindRotation = Quaternion.Normalize(
        Quaternion.CreateFromYawPitchRoll(0.35f, -0.20f, 0.15f));
    private static readonly Vector3 RootBindScale = Vector3.One;

    private static readonly Vector3 RootAnimatedTranslation = new(4.20f, -1.55f, 3.60f);
    private static readonly Quaternion RootAnimatedRotation = Quaternion.Normalize(
        Quaternion.CreateFromYawPitchRoll(-0.25f, 0.30f, -0.35f));
    private static readonly Vector3 RootAnimatedScale = new(1.05f, 0.95f, 1.10f);

    private static readonly Vector3 ChildBindTranslation = new(1.25f, 2.75f, -0.80f);
    private static readonly Quaternion ChildBindRotation = Quaternion.Normalize(
        Quaternion.CreateFromYawPitchRoll(-0.45f, 0.30f, 0.20f));
    private static readonly Vector3 ChildBindScale = Vector3.One;

    private static readonly Vector3 ChildAnimatedTranslation = new(2.10f, 2.20f, -0.15f);
    private static readonly Quaternion ChildAnimatedRotation = Quaternion.Normalize(
        Quaternion.CreateFromYawPitchRoll(0.40f, -0.35f, 0.55f));
    private static readonly Vector3 ChildAnimatedScale = new(0.75f, 1.30f, 0.95f);

    [Fact]
    public void Export_Blend_NonPsx_AbsoluteChannelsUseGeneralPoseBasis()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath) || !File.Exists(scriptPath))
            Assert.Skip(
                "Set NEVERSOFT_BLENDER_HELPER to blender.exe (and ensure BlenderExporter/import_package.py " +
                "is copied next to the test binary) to run this Blender round-trip regression.");

        using var temp = new TempDirectory();
        var (document, sourceVertices, childWeights, rootInverseBind, childInverseBind) = CreateDocument();

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Blend,
                BlenderHelperPath = helperPath
            });

        var blendPath = Assert.Single(result.OutputPaths);
        var report = InspectBlend(helperPath!, blendPath, temp.Path);

        Assert.Equal(2, report.Actions);

        // An absolute animation key equal to LocalTransform must reproduce the
        // authored bind pose. This catches both double translation and failure
        // to express bind rotation through edit-bone + pose-basis space.
        var expectedBind = sourceVertices.Select(ToBlenderWorld).ToArray();
        AssertVerticesClose(expectedBind, report.BindPoseVertices, 3e-4f, "bind pose");

        // Verify a genuinely different absolute local TRS against the same
        // row-vector skinning equation used by System.Numerics/the IR:
        // vertex * inverseBind * animatedGlobal.
        var animatedLocal = CreateLocalTransform(
            ChildAnimatedScale, ChildAnimatedRotation, ChildAnimatedTranslation);
        var animatedRoot = CreateLocalTransform(
            RootAnimatedScale, RootAnimatedRotation, RootAnimatedTranslation);
        var animatedGlobal = animatedLocal * animatedRoot;
        var rootSkinningMatrix = rootInverseBind * animatedRoot;
        var childSkinningMatrix = childInverseBind * animatedGlobal;
        var expectedAnimated = sourceVertices
            .Select((vertex, index) =>
            {
                var rootPosition = Vector3.Transform(vertex, rootSkinningMatrix);
                var childPosition = Vector3.Transform(vertex, childSkinningMatrix);
                var childWeight = childWeights[index];
                var blended = rootPosition * (1f - childWeight) + childPosition * childWeight;
                return ToBlenderWorld(blended);
            })
            .ToArray();
        Assert.True(Vector3.Distance(expectedBind[0], expectedAnimated[0]) > 0.25f,
            "Synthetic animated pose must differ materially from bind pose.");
        AssertVerticesClose(expectedAnimated, report.AnimatedPoseVertices, 5e-4f, "animated pose");
    }

    [Fact]
    public void Export_Blend_NonRigidBindFailsClearly()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath) || !File.Exists(scriptPath))
            Assert.Skip("Set NEVERSOFT_BLENDER_HELPER to blender.exe to run this Blender regression.");

        using var temp = new TempDirectory();
        var (document, _, _, _, _) = CreateDocument(new Vector3(2f, 1f, 1f));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModelExportService.Export(document, new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Blend,
                BlenderHelperPath = helperPath
            }));

        Assert.Contains("unsupported non-rigid bind matrix", exception.Message);
        Assert.Contains("child", exception.Message);
    }

    [Fact]
    public void Export_Blend_RealThps4Walk_MatchesGlbPose()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
            Assert.Skip("Set NEVERSOFT_BLENDER_HELPER to blender.exe to run this Blender regression.");
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        const string skeletonFileName = "Ped_F.ske";
        const string skinFileName = "skater_f.skin.ps2";
        const string animationFileName = "Ped_F_Walk.ska.ps2";
        var skeletonPath = paths.FindSampleFile(Thps4Build, skeletonFileName);
        var skinPath = paths.FindSampleFile(Thps4Build, skinFileName);
        var animationPath = paths.FindSampleFile(Thps4Build, animationFileName);
        Assert.SkipWhen(skeletonPath is null, $"Test file not found: {skeletonFileName}");
        Assert.SkipWhen(skinPath is null, $"Test file not found: {skinFileName}");
        Assert.SkipWhen(animationPath is null, $"Test file not found: {animationFileName}");

        var skeleton = SkeletonFile.Parse(skeletonPath!);
        var compressTable = SkaCommand.FindCompressTable(animationPath!);
        Assert.NotNull(compressTable);
        var animation = SkaFile.Parse(File.ReadAllBytes(animationPath!), compressTable);
        if (skeleton.Version == 1)
        {
            var defaultPath = SkaCommand.FindDefaultPoseFile(skeletonPath!, animationPath!);
            Assert.NotNull(defaultPath);
            var defaultTable = SkaCommand.FindCompressTable(defaultPath!);
            var defaultAnimation = SkaFile.Parse(File.ReadAllBytes(defaultPath!), defaultTable);
            skeleton = Ps2SkeletonDefaultPose.EnrichWithDefaultPose(skeleton, defaultAnimation);
        }

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(skinPath!),
            FileName = Path.GetFileName(skinPath!),
            OutputStem = "Ped_F_Walk",
            SourceKind = ModelSourceKind.Ps2Scene,
            PreparedSkeleton = skeleton,
            SkaAnimations = [("Ped_F_Walk", animation)]
        });

        // PS2 SKE stores rigid local bind transforms (rotation + translation).
        // Pin that premise before comparing the general rigid-basis conversion.
        var modelSkeleton = Assert.Single(document.Skeletons);
        foreach (var bone in modelSkeleton.Bones)
        {
            Assert.True(Matrix4x4.Decompose(bone.LocalTransform, out var scale, out _, out _));
            Assert.True(Vector3.Distance(Vector3.One, scale) < 1e-4f,
                $"Real PS2 bind bone '{bone.Name}' unexpectedly had scale {scale}.");
        }

        var worldBinds = new Matrix4x4[modelSkeleton.Bones.Count];
        for (var i = 0; i < modelSkeleton.Bones.Count; i++)
        {
            var bone = modelSkeleton.Bones[i];
            worldBinds[i] = bone.ParentIndex >= 0
                ? bone.LocalTransform * worldBinds[bone.ParentIndex]
                : bone.LocalTransform;
            Assert.True(Matrix4x4.Invert(worldBinds[i], out var expectedInverseBind));
            var difference = MaxMatrixDifference(expectedInverseBind, bone.InverseBindMatrix);
            Assert.True(difference < 1e-3f,
                $"Real PS2 bind bone '{bone.Name}' local hierarchy and inverse bind differ by {difference}.");
        }

        using var temp = new TempDirectory();
        var glbResult = ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = temp.Path,
            Format = MeshOutputFormat.Glb
        });
        var blendResult = ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = temp.Path,
            Format = MeshOutputFormat.Blend,
            BlenderHelperPath = helperPath
        });

        // Frame zero is an authored key for every emitted track, avoiding the
        // expected Blender quaternion-NLERP vs glTF-SLERP interpolation delta.
        const float sampleTime = 0f;
        var model = ModelRoot.Load(Assert.Single(glbResult.OutputPaths));
        var glbAnimation = Assert.Single(model.LogicalAnimations);
        var actual = InspectActionBoneMatrices(
            helperPath!, Assert.Single(blendResult.OutputPaths), temp.Path,
            "Ped_F_Walk", sampleTime * 24f);
        Assert.NotEmpty(actual);

        // Compare bone world poses directly. Some shipped PS2 vertices carry
        // tiny, non-normalized single weights; Blender normalizes those while
        // the software GLB loader does not, so mesh bounds are not a clean
        // transform-convention oracle. Bone matrices isolate the basis math.
        var hasAnimatedPoseDelta = false;
        for (var boneIndex = 0; boneIndex < modelSkeleton.Bones.Count; boneIndex++)
        {
            var bone = modelSkeleton.Bones[boneIndex];
            var glbNode = model.LogicalNodes.FirstOrDefault(node => node.Name == bone.Name);
            Assert.NotNull(glbNode);
            Assert.True(actual.TryGetValue(bone.Name, out var blendMatrix),
                $"Blender armature did not contain bone '{bone.Name}'.");
            var expectedMatrix = glbNode!.GetWorldMatrix(glbAnimation, sampleTime);
            hasAnimatedPoseDelta |= MaxMatrixDifference(expectedMatrix, worldBinds[boneIndex]) > 1e-3f;
            var difference = MaxMatrixDifference(expectedMatrix, blendMatrix);
            Assert.True(difference < 2e-3f,
                $"Real THPS4 bone '{bone.Name}' pose differed from GLB by {difference}.");
        }

        Assert.True(hasAnimatedPoseDelta,
            "The sampled real THPS4 key must differ from bind pose or this oracle is degenerate.");
    }

    private static (ModelDocument Document, Vector3[] SourceVertices, float[] ChildWeights,
        Matrix4x4 RootInverseBind, Matrix4x4 ChildInverseBind) CreateDocument(
        Vector3? childBindScaleOverride = null)
    {
        var document = new ModelDocument
        {
            Name = "non_psx_pose_basis",
            SourceKind = ModelSourceKind.Ps2Scene
        };
        document.Materials.Add(new RenderMaterial { Name = "mat", BaseColor = Vector4.One });

        var rootBind = CreateLocalTransform(RootBindScale, RootBindRotation, RootBindTranslation);
        var childBindScale = childBindScaleOverride ?? ChildBindScale;
        var childBindLocal = CreateLocalTransform(
            childBindScale, ChildBindRotation, ChildBindTranslation);
        var childBindGlobal = childBindLocal * rootBind;
        Assert.True(Matrix4x4.Invert(rootBind, out var rootInverseBind));
        Assert.True(Matrix4x4.Invert(childBindGlobal, out var childInverseBind));

        var skeleton = new ModelSkeleton { Name = "skeleton" };
        skeleton.Bones.Add(new ModelBone
        {
            Name = "root",
            ParentIndex = -1,
            LocalTransform = rootBind,
            InverseBindMatrix = rootInverseBind
        });
        skeleton.Bones.Add(new ModelBone
        {
            Name = "child",
            ParentIndex = 0,
            LocalTransform = childBindLocal,
            InverseBindMatrix = childInverseBind
        });
        document.Skeletons.Add(skeleton);

        var childLocalVertices = new[]
        {
            new Vector3(-0.60f, -0.25f, 0.10f),
            new Vector3( 0.85f, -0.10f, 0.25f),
            new Vector3( 0.70f,  1.10f, -0.15f),
            new Vector3(-0.45f,  0.95f, 0.35f)
        };
        var sourceVertices = childLocalVertices
            .Select(vertex => Vector3.Transform(vertex, childBindGlobal))
            .ToArray();
        var childWeights = new[] { 0.25f, 0.45f, 0.65f, 0.80f };
        var influences = childWeights
            .Select(childWeight => new ModelBoneInfluences(
                0, 1, 0, 0, 1f - childWeight, childWeight, 0f, 0f))
            .ToArray();

        var mesh = new ModelMesh { Name = "mesh" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "prim",
            MaterialIndex = 0,
            Vertices = sourceVertices.Select((position, index) =>
                new ModelVertex(position, Vector3.UnitZ, Vector4.One,
                    index switch
                    {
                        0 => Vector2.Zero,
                        1 => Vector2.UnitX,
                        2 => Vector2.One,
                        _ => Vector2.UnitY
                    })).ToArray(),
            Indices = [0, 1, 2, 0, 2, 3],
            Skin = new ModelSkinBinding
            {
                SkeletonIndex = 0,
                Influences = influences
            }
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode
        {
            Name = "node",
            MeshIndex = 0,
            Transform = Matrix4x4.Identity
        });

        document.Animations.Add(CreateConstantAnimation(
            "bind_pose",
            RootBindTranslation, RootBindRotation, RootBindScale,
            ChildBindTranslation, ChildBindRotation, childBindScale));
        document.Animations.Add(CreateConstantAnimation(
            "animated_pose",
            RootAnimatedTranslation, RootAnimatedRotation, RootAnimatedScale,
            ChildAnimatedTranslation, ChildAnimatedRotation, ChildAnimatedScale));

        return (document, sourceVertices, childWeights, rootInverseBind, childInverseBind);
    }

    private static ModelAnimation CreateConstantAnimation(
        string name,
        Vector3 rootTranslation,
        Quaternion rootRotation,
        Vector3 rootScale,
        Vector3 childTranslation,
        Quaternion childRotation,
        Vector3 childScale)
    {
        var animation = new ModelAnimation { Name = name };
        AddConstantTrsChannels(
            animation, 0, rootTranslation, rootRotation, rootScale);
        AddConstantTrsChannels(
            animation, 1, childTranslation, childRotation, childScale);
        return animation;
    }

    private static void AddConstantTrsChannels(
        ModelAnimation animation,
        int boneIndex,
        Vector3 translation,
        Quaternion rotation,
        Vector3 scale)
    {
        animation.Channels.Add(new ModelAnimationChannel
        {
            SkeletonIndex = 0,
            BoneIndex = boneIndex,
            Property = ModelAnimationProperty.Translation,
            Times = [0f, 1f],
            Values =
            [
                translation.X, translation.Y, translation.Z,
                translation.X, translation.Y, translation.Z
            ]
        });
        animation.Channels.Add(new ModelAnimationChannel
        {
            SkeletonIndex = 0,
            BoneIndex = boneIndex,
            Property = ModelAnimationProperty.Rotation,
            Times = [0f, 1f],
            Values =
            [
                rotation.X, rotation.Y, rotation.Z, rotation.W,
                rotation.X, rotation.Y, rotation.Z, rotation.W
            ]
        });
        animation.Channels.Add(new ModelAnimationChannel
        {
            SkeletonIndex = 0,
            BoneIndex = boneIndex,
            Property = ModelAnimationProperty.Scale,
            Times = [0f, 1f],
            Values =
            [
                scale.X, scale.Y, scale.Z,
                scale.X, scale.Y, scale.Z
            ]
        });
    }

    private static Matrix4x4 CreateLocalTransform(
        Vector3 scale, Quaternion rotation, Vector3 translation) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateFromQuaternion(rotation)
        * Matrix4x4.CreateTranslation(translation);

    private static Vector3 ToBlenderWorld(Vector3 source) =>
        new(source.X, -source.Z, source.Y);

    private static float MaxMatrixDifference(Matrix4x4 left, Matrix4x4 right)
    {
        var leftValues = new[]
        {
            left.M11, left.M12, left.M13, left.M14,
            left.M21, left.M22, left.M23, left.M24,
            left.M31, left.M32, left.M33, left.M34,
            left.M41, left.M42, left.M43, left.M44
        };
        var rightValues = new[]
        {
            right.M11, right.M12, right.M13, right.M14,
            right.M21, right.M22, right.M23, right.M24,
            right.M31, right.M32, right.M33, right.M34,
            right.M41, right.M42, right.M43, right.M44
        };
        return leftValues.Zip(rightValues, static (a, b) => MathF.Abs(a - b)).Max();
    }

    private static void AssertVerticesClose(
        Vector3[] expected,
        Vector3[] actual,
        float tolerance,
        string context)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var distance = Vector3.Distance(expected[i], actual[i]);
            Assert.True(distance <= tolerance,
                $"{context} vertex {i}: expected {expected[i]}, got {actual[i]} " +
                $"(distance {distance}, tolerance {tolerance}).");
        }
    }

    private static BlendReport InspectBlend(string helperPath, string blendPath, string tempDir)
    {
        var scriptPath = Path.Combine(tempDir, "inspect_pose_basis.py");
        var reportPath = Path.Combine(tempDir, "pose_basis_report.json");
        File.WriteAllText(scriptPath, InspectScript);

        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        foreach (var arg in new[] { "-b", "--factory-startup", "--python", scriptPath, "--", blendPath, reportPath })
            process.StartInfo.ArgumentList.Add(arg);

        Assert.True(process.Start(), "Failed to start Blender for .blend inspection.");
        var stderr = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(File.Exists(reportPath),
            $"Blender inspection produced no report (exit {process.ExitCode}).{Environment.NewLine}{stderr}");

        using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = json.RootElement;
        return new BlendReport(
            root.GetProperty("actions").GetInt32(),
            ReadVectors(root.GetProperty("bindPoseVertices")),
            ReadVectors(root.GetProperty("animatedPoseVertices")));
    }

    private static Vector3[] ReadVectors(JsonElement vectors) =>
        vectors.EnumerateArray()
            .Select(vector => new Vector3(
                vector[0].GetSingle(), vector[1].GetSingle(), vector[2].GetSingle()))
            .ToArray();

    private static Dictionary<string, Matrix4x4> InspectActionBoneMatrices(
        string helperPath,
        string blendPath,
        string tempDir,
        string actionName,
        float frame)
    {
        var scriptPath = Path.Combine(tempDir, "inspect_real_pose_basis.py");
        var reportPath = Path.Combine(tempDir, "real_pose_basis_report.json");
        File.WriteAllText(scriptPath, InspectActionScript);

        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        foreach (var arg in new[]
                 {
                     "-b", "--factory-startup", "--python", scriptPath, "--",
                     blendPath, reportPath, actionName, frame.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 })
            process.StartInfo.ArgumentList.Add(arg);

        Assert.True(process.Start(), "Failed to start Blender for real-rig inspection.");
        var stderr = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(File.Exists(reportPath),
            $"Blender real-rig inspection produced no report (exit {process.ExitCode})." +
            $"{Environment.NewLine}{stderr}");

        using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
        return json.RootElement.GetProperty("bones")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => ReadBlenderMatrix(property.Value),
                StringComparer.Ordinal);
    }

    private static Matrix4x4 ReadBlenderMatrix(JsonElement values)
    {
        var v = values.EnumerateArray().Select(static value => value.GetSingle()).ToArray();
        Assert.Equal(16, v.Length);
        // Blender stores column-vector matrices; transpose into the
        // System.Numerics row-vector convention used by ModelDocument/GLB.
        return new Matrix4x4(
            v[0], v[4], v[8], v[12],
            v[1], v[5], v[9], v[13],
            v[2], v[6], v[10], v[14],
            v[3], v[7], v[11], v[15]);
    }

    private readonly record struct BlendReport(
        int Actions,
        Vector3[] BindPoseVertices,
        Vector3[] AnimatedPoseVertices);

    private const string InspectScript = """
import bpy, sys, json

argv = sys.argv[sys.argv.index("--") + 1:]
blend_path, out_path = argv[0], argv[1]
bpy.ops.wm.open_mainfile(filepath=blend_path)
scene = bpy.context.scene
arm = next((obj for obj in scene.objects if obj.type == "ARMATURE"), None)


def evaluated_vertices_for(action_name):
    action = next((candidate for candidate in bpy.data.actions if action_name in candidate.name), None)
    if arm is not None and action is not None:
        animation_data = arm.animation_data or arm.animation_data_create()
        animation_data.action = action
        try:
            if hasattr(action, "slots") and len(action.slots):
                animation_data.action_slot = action.slots[0]
        except Exception:
            pass
    scene.frame_set(12)
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    result = []
    for obj in scene.objects:
        if obj.type != "MESH":
            continue
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        world = evaluated.matrix_world
        result.extend([list(world @ vertex.co) for vertex in mesh.vertices])
        evaluated.to_mesh_clear()
    return result


with open(out_path, "w") as handle:
    json.dump({
        "actions": len(bpy.data.actions),
        "bindPoseVertices": evaluated_vertices_for("bind_pose"),
        "animatedPoseVertices": evaluated_vertices_for("animated_pose"),
    }, handle)
""";

    private const string InspectActionScript = """
import bpy, sys, json

argv = sys.argv[sys.argv.index("--") + 1:]
blend_path, out_path, action_name, frame = argv[0], argv[1], argv[2], float(argv[3])
bpy.ops.wm.open_mainfile(filepath=blend_path)
scene = bpy.context.scene
arm = next((obj for obj in scene.objects if obj.type == "ARMATURE"), None)
action = next((candidate for candidate in bpy.data.actions if action_name in candidate.name), None)
if arm is not None and action is not None:
    animation_data = arm.animation_data or arm.animation_data_create()
    animation_data.action = action
    try:
        if hasattr(action, "slots") and len(action.slots):
            animation_data.action_slot = action.slots[0]
    except Exception:
        pass
scene.frame_set(int(frame), subframe=frame - int(frame))
bpy.context.view_layer.update()
depsgraph = bpy.context.evaluated_depsgraph_get()
bones = {
    bone.name: [value for row in bone.matrix for value in row]
    for bone in arm.pose.bones
}
with open(out_path, "w") as handle:
    json.dump({"bones": bones}, handle)
""";

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtBlendPoseBasis_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
