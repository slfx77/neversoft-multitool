using System.Diagnostics;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the THPS2 GBA skater's animation export: the clip/tick remap, the
///     bone-per-vertex rig (bind pose == the static export), fail-closed selection,
///     and the exported GLB's shape.
/// </summary>
public sealed class GbaSkaterAnimationTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");

    [Fact]
    public void ClipFrames_HonourTheTickRemapAndCoverTheWholePool()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom)!;
        var clips = GbaSkaterModel.ReadClips(rom, model);

        var referenced = new HashSet<int>();
        var nonEmpty = 0;
        var singlePose = 0;
        foreach (var clip in clips)
        {
            var frames = GbaSkaterModel.ClipFrames(rom, model, clip);
            Assert.Equal(clip.TickCount, frames.Length);
            Assert.All(frames, f => Assert.InRange(f, 0, model.FrameCount - 1));
            if (frames.Length == 0)
                continue;
            nonEmpty++;
            referenced.UnionWith(frames);
            if (frames.Distinct().Count() == 1)
                singlePose++;
        }

        // The four authored-empty clips; every other clip plays 5..97 ticks.
        Assert.Equal([65, 66, 84, 85],
            clips.Where(c => c.TickCount == 0).Select(c => c.Index).ToArray());
        Assert.Equal(217, nonEmpty);
        Assert.Equal(5, clips.Where(c => c.TickCount > 0).Min(c => c.TickCount));
        Assert.Equal(97, clips.Max(c => c.TickCount));
        Assert.Equal(51, singlePose);

        // Every pool frame is reachable through some clip — the remap addresses
        // the whole 4,772-frame pool, nothing is orphaned.
        Assert.Equal(model.FrameCount, referenced.Count);
    }

    [CorpusFact]
    public void AnimatedDocument_KeepsStaticGeometryAndBindPose()
    {
        var (rom, model, native) = LoadSpiderMan();

        var staticDocument = BuildStatic(native);
        var animatedDocument = ModelDocument.CreateNative(
            "13_spider_man", ModelSourceKind.GbaModel, native);
        Assert.Equal(1, GbaAnimatedModelWriter.TryPopulate(
            animatedDocument, native, [0], includeAllClips: false));

        // The static path stays skinless and animation-free.
        Assert.Empty(staticDocument.Animations);
        Assert.Empty(staticDocument.Skeletons);
        Assert.All(staticDocument.Meshes.SelectMany(m => m.Primitives), p => Assert.Null(p.Skin));

        // Bind geometry is the static geometry, primitive for primitive.
        Assert.Equal(staticDocument.TriangleCount, animatedDocument.TriangleCount);
        Assert.Equal(266, animatedDocument.TriangleCount);
        var expected = staticDocument.Meshes.SelectMany(m => m.Primitives).ToList();
        var actual = animatedDocument.Meshes.SelectMany(m => m.Primitives).ToList();
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Indices, actual[i].Indices);
            Assert.Equal(
                expected[i].Vertices.Select(v => v.Position),
                actual[i].Vertices.Select(v => v.Position));
            Assert.NotNull(actual[i].Skin);
        }

        // One bone per unique model vertex, every corner weighted 1 onto its own.
        var skeleton = Assert.Single(animatedDocument.Skeletons);
        var boneCount = model.VertCounts.Sum(c => c);
        Assert.Equal(172, boneCount);
        Assert.Equal(boneCount, skeleton.Bones.Count);
        Assert.All(skeleton.Bones, bone =>
        {
            Assert.Equal(-1, bone.ParentIndex);
            // Pure translations: bind identity is exact, not merely close.
            Assert.Equal(Matrix4x4.Identity, bone.LocalTransform * bone.InverseBindMatrix);
        });
        Assert.All(actual.SelectMany(p => p.Skin!.Influences), influence =>
        {
            Assert.InRange(influence.Joint0, 0, boneCount - 1);
            Assert.Equal(1f, influence.Weight0);
            Assert.Equal(0f, influence.Weight1);
        });

        // Frame 0 is the bind pose, so every bone sits on its static vertex.
        var frame0 = GbaSkaterModel.ReadFrameVertices(rom, model, 0);
        var bone = 0;
        for (var sub = 0; sub < GbaSkaterModel.SubObjectCount; sub++)
            foreach (var v in frame0[sub])
                Assert.Equal(
                    GbaModelGeometryWriter.ToGlb(v), skeleton.Bones[bone++].LocalTransform.Translation);
    }

    [CorpusFact]
    public void ClipKeys_ArePoolPosesInTickOrderNotAnAccumulation()
    {
        var (rom, model, native) = LoadSpiderMan();
        var clips = GbaSkaterModel.ReadClips(rom, model);

        // Clip 52 repeats frames (a hold) and is not a contiguous run — exactly the
        // case a frame-range playback would get wrong.
        const int holdClip = 52;
        var frames = GbaSkaterModel.ClipFrames(rom, model, clips[holdClip]);
        Assert.True(frames.Distinct().Count() < frames.Length, "clip 52 should contain holds");

        var document = ModelDocument.CreateNative("13_spider_man", ModelSourceKind.GbaModel, native);
        Assert.Equal(1, GbaAnimatedModelWriter.TryPopulate(
            document, native, [holdClip], includeAllClips: false));
        var animation = Assert.Single(document.Animations);
        Assert.Equal(172, animation.Channels.Count);

        // Every channel shares ONE times array instance and one key per tick.
        var times = animation.Channels[0].Times;
        Assert.All(animation.Channels, channel =>
        {
            Assert.Same(times, channel.Times);
            Assert.Equal(ModelAnimationProperty.Translation, channel.Property);
            Assert.Equal(ModelAnimationInterpolation.Linear, channel.Interpolation);
            Assert.Equal(frames.Length, channel.KeyCount);
        });
        for (var t = 0; t < times.Length; t++)
            Assert.Equal(t / GbaAnimatedModelWriter.TicksPerSecond, times[t]);

        // Each key is the pool pose the remap names for that tick, verbatim — the
        // per-frame anchor bytes (the pose AABB centre) are never added in.
        for (var t = 0; t < frames.Length; t++)
        {
            var pose = GbaSkaterModel.ReadFrameVertices(rom, model, frames[t]);
            var bone = 0;
            for (var sub = 0; sub < GbaSkaterModel.SubObjectCount; sub++)
                foreach (var v in pose[sub])
                {
                    var expected = GbaModelGeometryWriter.ToGlb(v);
                    var values = animation.Channels[bone++].Values;
                    Assert.Equal(expected.X, values[t * 3]);
                    Assert.Equal(expected.Y, values[t * 3 + 1]);
                    Assert.Equal(expected.Z, values[t * 3 + 2]);
                }
        }
    }

    [CorpusFact]
    public void EmptyAndInvalidClipSelections_FailClosed()
    {
        var (_, _, native) = LoadSpiderMan();

        // Empty clips, out-of-range and negative indices are skipped; the one valid
        // index still exports, and names carry the CLIP index, not a compacted one.
        var mixed = ModelDocument.CreateNative("13_spider_man", ModelSourceKind.GbaModel, native);
        Assert.Equal(1, GbaAnimatedModelWriter.TryPopulate(
            mixed, native, [65, 84, 500, -1, 3], includeAllClips: false));
        Assert.Equal("anim_3", Assert.Single(mixed.Animations).Name);

        // A selection with nothing valid leaves the document COMPLETELY untouched,
        // so the caller's static fallback produces the ordinary export.
        var rejected = ModelDocument.CreateNative("13_spider_man", ModelSourceKind.GbaModel, native);
        Assert.Equal(0, GbaAnimatedModelWriter.TryPopulate(
            rejected, native, [65, 66, 999], includeAllClips: false));
        Assert.Empty(rejected.Animations);
        Assert.Empty(rejected.Skeletons);
        Assert.Empty(rejected.Meshes);
        Assert.Empty(rejected.Materials);
    }

    [CorpusFact]
    public void AllClips_ExportInOneKhronosCleanGlb()
    {
        var (_, _, native) = LoadSpiderMan();
        var document = ModelDocument.CreateNative("13_spider_man", ModelSourceKind.GbaModel, native);
        Assert.Equal(217, GbaAnimatedModelWriter.TryPopulate(
            document, native, clipIndices: null, includeAllClips: true));

        // The four empty clips leave gaps: names keep the ROM's own clip index.
        Assert.DoesNotContain("anim_65", document.Animations.Select(a => a.Name));
        Assert.Contains("anim_64", document.Animations.Select(a => a.Name));
        Assert.Contains("anim_220", document.Animations.Select(a => a.Name));

        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.Equal(266, triangles);
        AssertKhronosClean(glb!);

        using var stream = new MemoryStream(glb!, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Equal(217, model.LogicalAnimations.Count);
        Assert.Equal(172, Assert.Single(model.LogicalSkins).JointsCount);
        Assert.All(model.LogicalMeshes.SelectMany(m => m.Primitives), primitive =>
        {
            Assert.NotNull(primitive.GetVertexAccessor("JOINTS_0"));
            Assert.NotNull(primitive.GetVertexAccessor("WEIGHTS_0"));
        });
    }

    private (byte[] Rom, GbaSkaterModel.ModelInfo Model, GbaModelNativeSource Native) LoadSpiderMan()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom)!;
        var record = rom.AsSpan(
            model.CharacterTableOffset + 13 * GbaSkaterModel.CharacterRecordSize,
            GbaSkaterModel.CharacterRecordSize).ToArray();
        return (rom, model, new GbaModelNativeSource(record, rom, 13, "Spider-Man", Outfit: 0));
    }

    private static ModelDocument BuildStatic(GbaModelNativeSource native)
    {
        var document = ModelDocument.CreateNative(
            "13_spider_man", ModelSourceKind.GbaModel, native);
        GbaModelGeometryWriter.Populate(document, native);
        return document;
    }

    private static void AssertKhronosClean(byte[] glb)
    {
        var validator = FindKhronosValidator();
        if (validator == null)
            return;

        var path = Path.Combine(
            Path.GetTempPath(), "nmt-gba-animation-" + Guid.NewGuid().ToString("N") + ".glb");
        try
        {
            File.WriteAllBytes(path, glb);
            var startInfo = new ProcessStartInfo
            {
                FileName = validator,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--stdout");
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var stdout = process!.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(60_000), "Khronos glTF Validator timed out");
            Assert.True(process.ExitCode == 0,
                $"Khronos glTF Validator exit {process.ExitCode}:{Environment.NewLine}{stderr}{stdout}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindKhronosValidator()
    {
        var directory = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && directory != null; depth++)
        {
            var candidate = Path.Combine(
                directory, "tools", "vendor", "gltf-validator", "gltf_validator.exe");
            if (File.Exists(candidate))
                return candidate;
            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
