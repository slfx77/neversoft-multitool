using System.Numerics;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class ThawCameraFovExportTests(TestPaths paths)
{
    private const uint PlatformFlag = 1u << 28;
    private const uint CameraFlag = 1u << 27;
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";
    private const string ThawGcBuild = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const string StorySelectPs2 =
        "DATAP/worlds/worldzones/z_storyselect/z_storyselect.pak/00000310.ska";
    private const string StorySelectGc =
        "worlds/worldzones/z_storyselect/z_storyselect.apk/Skater_camera.ska.ngc";

    [Fact]
    public void BuildSkeletonOnly_EligibleThawCamera_AttachesStaticProjectionToAnimatedBone()
    {
        const float horizontalFov = 0.17951635f;
        var animation = CreateAnimation(customKeys:
        [
            CreateFovKey(0, horizontalFov),
            CreateFovKey(120, 0.75f)
        ]);

        var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
            CreateFlatRig(animation), [("camera_move", animation)], "camera_move");

        var camera = Assert.Single(document.PerspectiveCameras);
        Assert.Equal(0, camera.SkeletonIndex);
        Assert.Equal(0, camera.BoneIndex);
        Assert.Equal(SkaModelDocumentBuilder.ThawCameraAspectRatio, camera.AspectRatio);
        Assert.Equal(0.13479553f, camera.VerticalFieldOfViewRadians, 7);
        Assert.Equal(SkaModelDocumentBuilder.ThawCameraZNear, camera.ZNear);
        Assert.Equal(SkaModelDocumentBuilder.ThawCameraZFar, camera.ZFar);

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.Equal(0, triangles);
        Assert.NotNull(glbBytes);

        using var stream = new MemoryStream(glbBytes, false);
        var model = ModelRoot.ReadGLB(stream);
        var gltfCamera = Assert.Single(model.LogicalCameras);
        Assert.Equal(camera.Name, gltfCamera.Name);
        var perspective = Assert.IsType<CameraPerspective>(gltfCamera.Settings);
        Assert.Equal(4f / 3f, perspective.AspectRatio);
        Assert.Equal(0.13479553f, perspective.VerticalFOV, 7);
        Assert.Equal(1f, perspective.ZNear);
        Assert.Equal(100_000f, perspective.ZFar);

        var cameraNode = Assert.Single(model.LogicalNodes, static node => node.Camera != null);
        Assert.Same(gltfCamera, cameraNode.Camera);
        Assert.NotNull(cameraNode.VisualParent);
        Assert.Single(model.DefaultScene!.VisualChildren);
        Assert.Equal(2, model.LogicalNodes.Count);

        var gltfAnimation = Assert.Single(model.LogicalAnimations);
        var channel = Assert.Single(gltfAnimation.Channels);
        Assert.Same(cameraNode, channel.TargetNode);
        Assert.Equal(PropertyPath.translation, channel.TargetNodePath);
    }

    [Fact]
    public void BuildSkeletonOnly_CameraProjectionGate_RejectsUnsupportedOrInvalidInputs()
    {
        var cases = new SkaAnimation[]
        {
            CreateAnimation(version: 0x20),
            CreateAnimation(flags: CameraFlag),
            CreateAnimation(flags: PlatformFlag),
            CreateAnimation(trackCount: 2),
            CreateAnimation(customKeys: []),
            CreateAnimation(customKeys: [CreateFovKey(1, 0.5f)]),
            CreateAnimation(customKeys: [CreateFovKey(0, 0f)]),
            CreateAnimation(customKeys: [CreateFovKey(0, float.NaN)]),
            CreateAnimation(customKeys: [CreateFovKey(0, MathF.PI)]),
            CreateAnimation(customKeys:
            [
                CreateFovKey(0, 0.5f),
                CreateFovKey(0, float.NaN)
            ])
        };

        foreach (var animation in cases)
        {
            var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
                CreateFlatRig(animation), [("unsupported", animation)], "unsupported");
            Assert.Empty(document.PerspectiveCameras);
        }
    }

    [Fact]
    public void BuildSkeletonOnly_RepeatedFrameZeroFov_LastSerializedValidEventWins()
    {
        var animation = CreateAnimation(customKeys:
        [
            CreateFovKey(0, 0.4f),
            CreateFovKey(0, float.NaN),
            CreateFovKey(0, 0.8f),
            CreateFovKey(60, 1.2f)
        ]);

        var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
            CreateFlatRig(animation), [("camera", animation)], "camera");

        var camera = Assert.Single(document.PerspectiveCameras);
        Assert.Equal(
            SkaModelDocumentBuilder.HorizontalToVerticalFov(
                0.8f, SkaModelDocumentBuilder.ThawCameraAspectRatio),
            camera.VerticalFieldOfViewRadians);
        Assert.Equal(4, animation.CustomKeys.Length);
        Assert.Equal(1.2f, animation.CustomKeys[^1].Fov);
    }

    [Fact]
    public void BuildSkeletonOnly_MultipleCameraAnimations_DoesNotCreateAmbiguousBinding()
    {
        var first = CreateAnimation();
        var second = CreateAnimation(customKeys: [CreateFovKey(0, 0.8f)]);

        var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
            CreateFlatRig(first), [("first", first), ("second", second)], "camera_set");

        Assert.Empty(document.PerspectiveCameras);
        Assert.Equal(2, document.Animations.Count);
    }

    [Fact]
    public void StorySelect_Ps2AndGc_ProduceEqualFrameZeroProjection()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2Path = GetFixturePath(ThawPs2Build, StorySelectPs2);
        var gcPath = GetFixturePath(ThawGcBuild, StorySelectGc);
        Assert.SkipWhen(!File.Exists(ps2Path) || !File.Exists(gcPath),
            "THAW StorySelect camera fixtures not found");

        var ps2 = SkaFile.Parse(File.ReadAllBytes(ps2Path));
        var gc = SkaFile.Parse(File.ReadAllBytes(gcPath));
        var ps2Horizontal = Assert.Single(ps2.CustomKeys, static key => key is { Type: 1, Timestamp: 0 }).Fov;
        var gcHorizontal = Assert.Single(gc.CustomKeys, static key => key is { Type: 1, Timestamp: 0 }).Fov;
        Assert.Equal(0.17951635f, ps2Horizontal);
        Assert.Equal(ps2Horizontal, gcHorizontal);

        var ps2Document = SkaModelDocumentBuilder.BuildSkeletonOnly(
            CreateFlatRig(ps2), [("StorySelect", ps2)], "StorySelect");
        var gcDocument = SkaModelDocumentBuilder.BuildSkeletonOnly(
            CreateFlatRig(gc), [("StorySelect", gc)], "StorySelect");
        var ps2Camera = Assert.Single(ps2Document.PerspectiveCameras);
        var gcCamera = Assert.Single(gcDocument.PerspectiveCameras);
        Assert.Equal(0.13479553f, ps2Camera.VerticalFieldOfViewRadians, 7);
        Assert.Equal(ps2Camera.VerticalFieldOfViewRadians, gcCamera.VerticalFieldOfViewRadians);
    }

    [Fact]
    public void StorySelect_Ps2AndGc_SkaCliDefaultRemainsGlbWithStaticCamera()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var fixtures = new[]
        {
            (Path: GetFixturePath(ThawPs2Build, StorySelectPs2), Stem: "00000310"),
            (Path: GetFixturePath(ThawGcBuild, StorySelectGc), Stem: "Skater_camera")
        };
        Assert.SkipWhen(fixtures.Any(static fixture => !File.Exists(fixture.Path)),
            "THAW StorySelect camera fixtures not found");

        using var temp = new TempDirectory();
        foreach (var (fixture, stem) in fixtures)
        {
            var output = Path.Combine(temp.Path, stem);
            var exitCode = SkaCommand.Create()
                .Parse([fixture, "--output", output])
                .Invoke();

            Assert.Equal(0, exitCode);
            var glbPath = Path.Combine(output, stem + ".glb");
            Assert.True(File.Exists(glbPath));
            Assert.False(File.Exists(Path.Combine(output, stem + ".blend")));
            using var stream = File.OpenRead(glbPath);
            var model = ModelRoot.ReadGLB(stream);
            var camera = Assert.Single(model.LogicalCameras);
            var perspective = Assert.IsType<CameraPerspective>(camera.Settings);
            Assert.Equal(0.13479553f, perspective.VerticalFOV, 7);
        }
    }

    [Fact]
    public void StorySelect_Ps2AndGc_SkaCliBothFormatRoutesEveryOutputThroughConfiguredHelper()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath),
            "Set NEVERSOFT_BLENDER_HELPER to Blender 5.1 for the StorySelect .blend CLI oracle");
        var fixtures = new[]
        {
            (Path: GetFixturePath(ThawPs2Build, StorySelectPs2), Stem: "00000310", Format: "both"),
            (Path: GetFixturePath(ThawGcBuild, StorySelectGc), Stem: "Skater_camera", Format: "both")
        };
        Assert.SkipWhen(fixtures.Any(static fixture => !File.Exists(fixture.Path)),
            "THAW StorySelect camera fixtures not found");

        using var temp = new TempDirectory();
        foreach (var (fixture, stem, format) in fixtures)
        {
            var output = Path.Combine(temp.Path, stem);
            var exitCode = SkaCommand.Create()
                .Parse([
                    fixture,
                    "--output", output,
                    "--format", format,
                    "--blender-helper", helperPath!
                ])
                .Invoke();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(output, stem + ".blend")));
            var glbPath = Path.Combine(output, stem + ".glb");
            Assert.True(File.Exists(glbPath));
            Assert.True(File.Exists(Path.Combine(output, stem + ".ska.json")));
            using var stream = File.OpenRead(glbPath);
            var model = ModelRoot.ReadGLB(stream);
            var perspective = Assert.IsType<CameraPerspective>(Assert.Single(model.LogicalCameras).Settings);
            Assert.Equal(0.13479553f, perspective.VerticalFOV, 7);
        }
    }

    [CorpusFact]
    public void GcCameraMasters_StaticFovEligibilityCensus_IsStable()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(ThawGcBuild, "*.ska.ngc").ToArray();
        Assert.True(files.Length >= 7_000, $"expected full THAW GC extraction, found {files.Length}");

        var cameraMasters = 0;
        var oneTrackCameraMasters = 0;
        var cameraMastersWithCustomEvents = 0;
        var fovBearingCameraMasters = 0;
        var cameraMastersWithoutFov = 0;
        var eligibleStaticCameras = 0;
        var totalFovKeys = 0;
        var frameZeroFovKeys = 0;
        var firstFov = 0;
        var firstScript = 0;
        var nonCameraFovFiles = 0;

        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (!SkaThawParser.IsThawSka(data, out var bigEndian))
                continue;

            var reader = new EndianSpanReader(data, bigEndian);
            var flags = reader.U32(4);
            var customCount = reader.U16(0x12);
            var isCamera = (flags & CameraFlag) != 0;
            if (!isCamera && customCount == 0)
                continue;

            var animation = SkaFile.Parse(data);
            var fovKeys = animation.CustomKeys.Where(static key => key.Type == 1).ToArray();
            if (!isCamera)
            {
                if (fovKeys.Length > 0)
                    nonCameraFovFiles++;
                continue;
            }

            cameraMasters++;
            Assert.True((flags & PlatformFlag) != 0,
                $"camera master {Path.GetFileName(file)} is not PLATFORM");
            if (animation.BoneTracks.Length == 1)
                oneTrackCameraMasters++;

            if (animation.CustomKeys.Length > 0)
            {
                cameraMastersWithCustomEvents++;
                if (animation.CustomKeys[0].Type == 1) firstFov++;
                if (animation.CustomKeys[0].Type == 4) firstScript++;
            }

            if (fovKeys.Length == 0)
            {
                cameraMastersWithoutFov++;
            }
            else
            {
                fovBearingCameraMasters++;
                totalFovKeys += fovKeys.Length;
                var frameZero = fovKeys.Count(static key =>
                    key.Timestamp == 0 && key.Fov is > 0f and < MathF.PI &&
                    float.IsFinite(key.Fov.Value));
                frameZeroFovKeys += frameZero;
                Assert.Equal(1, frameZero);
            }

            var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
                CreateFlatRig(animation), [("camera", animation)], "camera");
            if (document.PerspectiveCameras.Count == 1)
                eligibleStaticCameras++;
            else
                Assert.Empty(document.PerspectiveCameras);
        }

        Assert.Equal(347, cameraMasters);
        Assert.Equal(347, oneTrackCameraMasters);
        Assert.Equal(35, cameraMastersWithCustomEvents);
        Assert.Equal(35, fovBearingCameraMasters);
        Assert.Equal(312, cameraMastersWithoutFov);
        Assert.Equal(35, eligibleStaticCameras);
        Assert.Equal(391, totalFovKeys);
        Assert.Equal(35, frameZeroFovKeys);
        Assert.Equal(11, firstFov);
        Assert.Equal(24, firstScript);
        Assert.Equal(0, nonCameraFovFiles);
    }

    private string GetFixturePath(string build, string relativePath) =>
        Path.Combine(paths.SampleBuildsDir!, build,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static SkaAnimation CreateAnimation(
        uint version = 0x28,
        uint flags = PlatformFlag | CameraFlag,
        int trackCount = 1,
        SkaCustomKey[]? customKeys = null)
    {
        var tracks = new SkaBoneTrack[trackCount];
        for (var i = 0; i < trackCount; i++)
        {
            tracks[i] = new SkaBoneTrack
            {
                BoneIndex = i,
                BoneNameChecksum = 0xDEAD_BEEFu + (uint)i,
                RotationKeys = [new SkaRotationKey(0f, Quaternion.Identity)],
                TranslationKeys =
                [
                    new SkaTranslationKey(0f, new Vector3(1f, 2f, 3f)),
                    new SkaTranslationKey(1f, new Vector3(4f, 5f, 6f))
                ]
            };
        }

        return new SkaAnimation
        {
            Version = version,
            Flags = flags,
            Duration = 1f,
            BoneTracks = tracks,
            CustomKeys = customKeys ?? [CreateFovKey(0, 0.17951635f)]
        };
    }

    private static SkaCustomKey CreateFovKey(uint timestamp, float fov) => new()
    {
        Timestamp = timestamp,
        Type = 1,
        Size = 16,
        Payload = BitConverter.GetBytes(fov),
        Fov = fov
    };

    private static Ps2Skeleton CreateFlatRig(SkaAnimation animation)
    {
        var bones = new Ps2Bone[animation.BoneTracks.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            bones[i] = new Ps2Bone
            {
                NameChecksum = animation.BoneTracks[i].BoneNameChecksum ?? (uint)i,
                ParentChecksum = 0,
                FlipChecksum = 0,
                ParentIndex = -1,
                LocalRotation = Quaternion.Identity,
                LocalTranslation = Vector3.Zero,
                InverseBindMatrix = Matrix4x4.Identity
            };
        }

        return new Ps2Skeleton { Version = 2, Flags = 0, Bones = bones };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "nmt-thaw-camera-cli-" + Guid.NewGuid().ToString("N"));
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
                // External Blender processes may release output files just after exit.
            }
        }
    }
}
