using System.Diagnostics;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the THPS2 GBA skater's animation export: the clip/tick remap, the
///     morph-target representation (the engine blends complete posed vertex sets;
///     it has no skeleton), fail-closed selection, and the exported GLB's shape.
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
    public void AnimatedDocument_KeepsTheStaticGeometryAsItsBaseMesh()
    {
        var (_, _, native) = LoadSpiderMan();

        var staticDocument = BuildStatic(native);
        var animatedDocument = ModelDocument.CreateNative(
            "13_spider_man", ModelSourceKind.GbaModel, native);
        Assert.True(GbaAnimatedModelWriter.TryPopulate(animatedDocument, native, clipIndex: 20));

        // The static path carries no animation and no morph data at all.
        Assert.Empty(staticDocument.Animations);
        Assert.All(staticDocument.Meshes.SelectMany(m => m.Primitives), p => Assert.Null(p.MorphTargets));

        // The animated base mesh IS the static mesh: same triangles, same
        // topology, same positions. Only targets and a weights track are added.
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
            Assert.Null(expected[i].Skin);
            Assert.Null(actual[i].Skin);
        }

        // No skeleton is invented for a model that has none.
        Assert.Empty(animatedDocument.Skeletons);
    }

    [CorpusFact]
    public void MorphTargetsAndWeightsReproduceEveryTicksPose()
    {
        var (rom, model, native) = LoadSpiderMan();
        var clips = GbaSkaterModel.ReadClips(rom, model);

        // Clip 52 repeats frames (a hold) and is not a contiguous run — exactly
        // the case a frame-range playback would get wrong.
        const int holdClip = 52;
        var ticks = GbaSkaterModel.ClipFrames(rom, model, clips[holdClip]);
        Assert.True(ticks.Distinct().Count() < ticks.Length, "clip 52 should contain holds");

        var document = ModelDocument.CreateNative("13_spider_man", ModelSourceKind.GbaModel, native);
        Assert.True(GbaAnimatedModelWriter.TryPopulate(document, native, holdClip));
        var animation = Assert.Single(document.Animations);
        var channel = Assert.IsType<ModelMorphChannel>(animation.MorphChannel);

        // Targets are the clip's DISTINCT frames, not one per tick.
        Assert.Equal(ticks.Distinct().Count(), channel.TargetCount);
        Assert.Equal(ticks.Length, channel.KeyCount);
        Assert.Empty(animation.Channels);
        for (var tick = 0; tick < ticks.Length; tick++)
            Assert.Equal(tick / GbaAnimatedModelWriter.TicksPerSecond, channel.Times[tick]);

        // Every key applies exactly one target at full weight, and base + that
        // target is the ROM's pose for the tick — the per-frame anchor bytes
        // (the pose AABB centre) are never added in.
        var mesh = document.Meshes[channel.MeshIndex];
        foreach (var primitive in mesh.Primitives)
        {
            Assert.NotNull(primitive.MorphTargets);
            Assert.Equal(channel.TargetCount, primitive.MorphTargets!.Count);
            Assert.All(primitive.MorphTargets,
                t => Assert.Equal(primitive.Vertices.Length, t.PositionDeltas.Length));
        }

        for (var tick = 0; tick < ticks.Length; tick++)
        {
            var applied = Enumerable.Range(0, channel.TargetCount)
                .Where(t => channel.Weights[tick * channel.TargetCount + t] != 0f)
                .ToArray();
            var target = Assert.Single(applied);
            Assert.Equal(1f, channel.Weights[tick * channel.TargetCount + target]);

            var pose = GbaSkaterModel.ReadFrameVertices(rom, model, ticks[tick])
                .SelectMany(sub => sub.Select(GbaModelGeometryWriter.ToGlb))
                .ToHashSet();
            foreach (var primitive in mesh.Primitives)
            {
                var deltas = primitive.MorphTargets![target].PositionDeltas;
                for (var v = 0; v < primitive.Vertices.Length; v++)
                    Assert.Contains(primitive.Vertices[v].Position + deltas[v], pose);
            }
        }
    }

    [CorpusFact]
    public void EmptyAndInvalidClipSelections_FailClosed()
    {
        var (_, _, native) = LoadSpiderMan();

        foreach (var clip in (int[])[65, 66, 84, 85, 500, -1])
        {
            var document = ModelDocument.CreateNative("13_spider_man", ModelSourceKind.GbaModel, native);
            Assert.False(GbaAnimatedModelWriter.TryPopulate(document, native, clip));

            // Nothing was added, so the caller's static fallback produces the
            // ordinary export rather than a degraded animated one.
            Assert.Empty(document.Animations);
            Assert.Empty(document.Meshes);
            Assert.Empty(document.Materials);
        }
    }

    [CorpusFact]
    public void OneClipExportsAsAKhronosCleanMorphGlb()
    {
        var (rom, model, native) = LoadSpiderMan();
        var document = ModelDocument.CreateNative("13_spider_man", ModelSourceKind.GbaModel, native);
        Assert.True(GbaAnimatedModelWriter.TryPopulate(document, native, clipIndex: 20));

        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.Equal(266, triangles);
        AssertKhronosClean(glb!);

        using var stream = new MemoryStream(glb!, writable: false);
        var exported = ModelRoot.ReadGLB(stream);

        // The cart names clip 20 "Kickflip"; the clip carries its index because a
        // case-twin (KICKFLIP, clip 149) exists.
        var animation = Assert.Single(exported.LogicalAnimations);
        Assert.Equal("Kickflip (20)", animation.Name);

        // A weights track, no skin, no joints — this model has no skeleton.
        Assert.Empty(exported.LogicalSkins);
        var targetCount = GbaSkaterModel
            .ClipFrames(rom, model, GbaSkaterModel.ReadClips(rom, model)[20]).Distinct().Count();
        Assert.All(exported.LogicalMeshes.SelectMany(m => m.Primitives), primitive =>
        {
            Assert.Equal(targetCount, primitive.MorphTargetsCount);
            Assert.Null(primitive.GetVertexAccessor("JOINTS_0"));
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
