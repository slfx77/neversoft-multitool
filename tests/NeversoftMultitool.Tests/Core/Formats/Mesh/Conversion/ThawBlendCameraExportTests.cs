using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class ThawBlendCameraExportTests
{
    [Fact]
    public void BlendPackageWriter_SkeletonOnlyCamera_WritesValidatedManifestAndAnimationBuffers()
    {
        var document = CreateCameraDocument();
        document.PerspectiveCameras.Add(new ModelPerspectiveCamera
        {
            Name = "bad_index",
            SkeletonIndex = 7,
            BoneIndex = 0,
            AspectRatio = 4f / 3f,
            VerticalFieldOfViewRadians = 0.7f,
            ZNear = 1f,
            ZFar = 100_000f
        });
        document.PerspectiveCameras.Add(new ModelPerspectiveCamera
        {
            Name = "bad_projection",
            SkeletonIndex = 0,
            BoneIndex = 0,
            AspectRatio = 4f / 3f,
            VerticalFieldOfViewRadians = float.NaN,
            ZNear = 1f,
            ZFar = 100_000f
        });
        document.PerspectiveCameras.Add(new ModelPerspectiveCamera
        {
            Name = "bad_bone",
            SkeletonIndex = 0,
            BoneIndex = 9,
            AspectRatio = 4f / 3f,
            VerticalFieldOfViewRadians = 0.7f,
            ZNear = 1f,
            ZFar = 100_000f
        });

        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "camera.blend");

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("buffers/anim_0000_ch_0000.times.bin"));
        Assert.NotNull(archive.GetEntry("buffers/anim_0000_ch_0000.values.bin"));
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var camera = Assert.Single(manifest.RootElement.GetProperty("PerspectiveCameras").EnumerateArray());
        Assert.Equal("story_camera", camera.GetProperty("Name").GetString());
        Assert.Equal(0, camera.GetProperty("SkeletonIndex").GetInt32());
        Assert.Equal(0, camera.GetProperty("BoneIndex").GetInt32());
        Assert.Equal(4f / 3f, camera.GetProperty("AspectRatio").GetSingle());
        Assert.Equal(0.6f, camera.GetProperty("VerticalFieldOfViewRadians").GetSingle());
        Assert.Equal(1f, camera.GetProperty("ZNear").GetSingle());
        Assert.Equal(100_000f, camera.GetProperty("ZFar").GetSingle());
    }

    [Fact]
    public void BlendPackageWriter_TrulyEmptyDocument_StillRejects()
    {
        var document = new ModelDocument { Name = "empty" };
        using var payload = new MemoryStream();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BlendPackageWriter.Write(document, payload, "empty.blend"));

        Assert.Contains("geometry or a non-empty skeleton", exception.Message);
    }

    [Fact]
    public void Export_Blend_CameraCopiesAnimatedBoneWorldTransformAndProjection()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath) || !File.Exists(scriptPath))
            Assert.Skip(
                "Set NEVERSOFT_BLENDER_HELPER to Blender 5.1 and make the packaged importer available " +
                "to run this camera oracle.");

        using var temp = new TempDirectory();
        var result = ModelExportService.Export(CreateCameraDocument(), new MeshExportRequest
        {
            OutputDirectory = temp.Path,
            OutputStem = "story_camera",
            Format = MeshOutputFormat.Blend,
            BlenderHelperPath = helperPath
        });

        var blendPath = Assert.Single(result.OutputPaths);
        Assert.Equal(0, result.Triangles);
        var report = InspectBlend(helperPath!, blendPath, temp.Path);

        Assert.Equal("PERSP", report.Type);
        Assert.Equal("VERTICAL", report.SensorFit);
        Assert.Equal("FOV", report.LensUnit);
        Assert.Equal(0.6f, report.AngleY, 5);
        Assert.Equal(1f, report.ClipStart);
        Assert.Equal(100_000f, report.ClipEnd);
        Assert.Equal(4f / 3f, report.AspectRatio, 6);
        Assert.Equal("WORLD", report.OwnerSpace);
        Assert.Equal("WORLD", report.TargetSpace);
        Assert.Equal("child_camera", report.Subtarget);
        Assert.Equal(1, report.Actions);
        Assert.True(report.ActiveAction);
        Assert.True(report.ActionsHaveFakeUser);
        Assert.Equal(2, report.Frames.Count);
        foreach (var frame in report.Frames)
            AssertMatrixClose(frame.BoneWorld, frame.CameraWorld, 1e-5f);
        Assert.True(MatrixDistance(report.Frames[0].CameraWorld, report.Frames[1].CameraWorld) > 0.1f,
            "Synthetic camera animation must move between sampled frames.");
    }

    private static ModelDocument CreateCameraDocument()
    {
        var document = new ModelDocument { Name = "story_camera" };
        var skeleton = new ModelSkeleton
        {
            Name = "camera_rig",
            RootTransform = Matrix4x4.CreateFromQuaternion(
                                Quaternion.CreateFromYawPitchRoll(0.2f, -0.15f, 0.1f)) *
                            Matrix4x4.CreateTranslation(7f, -3f, 4f)
        };

        // Deliberately non-topological: the camera child precedes its parent.
        skeleton.Bones.Add(new ModelBone
        {
            Name = "child_camera",
            ParentIndex = 1,
            LocalTransform = Matrix4x4.CreateFromQuaternion(
                                 Quaternion.CreateFromYawPitchRoll(-0.3f, 0.25f, 0.15f)) *
                             Matrix4x4.CreateTranslation(2f, 3f, -1f)
        });
        skeleton.Bones.Add(new ModelBone
        {
            Name = "parent_root",
            ParentIndex = -1,
            LocalTransform = Matrix4x4.CreateFromQuaternion(
                                 Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, -0.1f)) *
                             Matrix4x4.CreateTranslation(-4f, 1f, 6f)
        });
        document.Skeletons.Add(skeleton);

        var animation = new ModelAnimation { Name = "camera_move" };
        animation.Channels.Add(new ModelAnimationChannel
        {
            SkeletonIndex = 0,
            BoneIndex = 0,
            Property = ModelAnimationProperty.Translation,
            Times = [0f, 1f],
            Values = [2f, 3f, -1f, 8f, -2f, 5f]
        });
        animation.Channels.Add(new ModelAnimationChannel
        {
            SkeletonIndex = 0,
            BoneIndex = 0,
            Property = ModelAnimationProperty.Rotation,
            Times = [0f, 1f],
            Values = Flatten(
                Quaternion.CreateFromYawPitchRoll(-0.3f, 0.25f, 0.15f),
                Quaternion.CreateFromYawPitchRoll(0.5f, -0.4f, 0.35f))
        });
        document.Animations.Add(animation);
        document.PerspectiveCameras.Add(new ModelPerspectiveCamera
        {
            Name = "story_camera",
            SkeletonIndex = 0,
            BoneIndex = 0,
            AspectRatio = 4f / 3f,
            VerticalFieldOfViewRadians = 0.6f,
            ZNear = 1f,
            ZFar = 100_000f
        });
        return document;
    }

    private static float[] Flatten(Quaternion first, Quaternion second) =>
        [first.X, first.Y, first.Z, first.W, second.X, second.Y, second.Z, second.W];

    private static CameraReport InspectBlend(string helperPath, string blendPath, string tempDirectory)
    {
        var inspectScript = Path.Combine(tempDirectory, "inspect_camera.py");
        var reportPath = Path.Combine(tempDirectory, "camera_report.json");
        File.WriteAllText(inspectScript, BlenderInspectionScript);

        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.ArgumentList.Add("--background");
        process.StartInfo.ArgumentList.Add(blendPath);
        process.StartInfo.ArgumentList.Add("--python-exit-code");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("--python");
        process.StartInfo.ArgumentList.Add(inspectScript);
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(reportPath);

        Assert.True(process.Start(), "Failed to start Blender for camera inspection.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && File.Exists(reportPath),
            $"Blender camera inspection failed ({process.ExitCode}).{Environment.NewLine}{stdout}" +
            Environment.NewLine + stderr);
        return JsonSerializer.Deserialize<CameraReport>(File.ReadAllText(reportPath))!;
    }

    private static void AssertMatrixClose(float[][] expected, float[][] actual, float tolerance)
    {
        Assert.Equal(4, expected.Length);
        Assert.Equal(4, actual.Length);
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
                Assert.InRange(MathF.Abs(expected[row][column] - actual[row][column]), 0f, tolerance);
        }
    }

    private static float MatrixDistance(float[][] first, float[][] second)
    {
        var sum = 0f;
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var delta = first[row][column] - second[row][column];
                sum += delta * delta;
            }
        }
        return MathF.Sqrt(sum);
    }

    private sealed record CameraReport(
        string Type,
        string SensorFit,
        string LensUnit,
        float AngleY,
        float ClipStart,
        float ClipEnd,
        float AspectRatio,
        string OwnerSpace,
        string TargetSpace,
        string Subtarget,
        int Actions,
        bool ActiveAction,
        bool ActionsHaveFakeUser,
        List<CameraFrame> Frames);

    private sealed record CameraFrame(float[][] CameraWorld, float[][] BoneWorld);

    private const string BlenderInspectionScript = """
        import bpy
        import json
        import sys

        report_path = sys.argv[sys.argv.index('--') + 1]
        scene = bpy.context.scene
        camera = scene.camera
        constraint = next(item for item in camera.constraints if item.type == 'COPY_TRANSFORMS')
        armature = constraint.target
        pose_bone = armature.pose.bones[constraint.subtarget]

        def matrix_values(matrix):
            return [[float(matrix[row][column]) for column in range(4)] for row in range(4)]

        frames = []
        for frame in (0, int(scene.render.fps)):
            scene.frame_set(frame)
            bpy.context.view_layer.update()
            frames.append({
                'CameraWorld': matrix_values(camera.matrix_world),
                'BoneWorld': matrix_values(armature.matrix_world @ pose_bone.matrix),
            })

        aspect = (
            scene.render.resolution_x * scene.render.pixel_aspect_x /
            (scene.render.resolution_y * scene.render.pixel_aspect_y)
        )
        with open(report_path, 'w', encoding='utf-8') as stream:
            json.dump({
                'Type': camera.data.type,
                'SensorFit': camera.data.sensor_fit,
                'LensUnit': camera.data.lens_unit,
                'AngleY': camera.data.angle_y,
                'ClipStart': camera.data.clip_start,
                'ClipEnd': camera.data.clip_end,
                'AspectRatio': aspect,
                'OwnerSpace': constraint.owner_space,
                'TargetSpace': constraint.target_space,
                'Subtarget': constraint.subtarget,
                'Actions': len(bpy.data.actions),
                'ActiveAction': (
                    armature.animation_data is not None and
                    armature.animation_data.action is not None
                ),
                'ActionsHaveFakeUser': all(action.use_fake_user for action in bpy.data.actions),
                'Frames': frames,
            }, stream)
        """;

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "nmt-thaw-camera-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // Blender can briefly retain a file handle during process teardown.
            }
        }
    }
}
