using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the Downhill Jam rider's animated export: one morph target per
///     distinct posed vertex set of a clip, the clip's own frame 0 as the base
///     mesh, every clip of the directory animating including the last, and
///     fail-closed refusal of a clip with no decoded length.
///     Model 19 is the live gameplay oracle used to close the format.
/// </summary>
public sealed class GbaDhjAnimationTests(TestPaths paths)
{
    // 12 pose records, all 12 distinct, so every record after frame 0 moves.
    private const int RuntimeVerifiedClip = 79;

    // 24 pose records whose frame 1 repeats frame 0 byte for byte — the case that
    // must contribute no target, because an all-zero target is dropped when the
    // glTF is written and would shift every later target's index.
    private const int ClipThatReturnsToItsBasePose = 18;
    private const int FrameRepeatingTheBasePose = 1;

    // The directory's last clip: no following offset bounds it, so its length
    // comes solely from the u32 prefix in front of it.
    private const int FinalClip = 93;

    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
        "Tony Hawk's Downhill Jam (USA).gba");

    [CorpusFact]
    public void AnimatedClip_CarriesOneNamedTargetPerDistinctMovedPose()
    {
        var (rom, model, library) = Load();
        var document = GbaDhjAnimatedModelWriter.TryBuild(
            rom, model, library, RuntimeVerifiedClip, "rider_19");
        Assert.NotNull(document);

        var animation = Assert.Single(document!.Animations);
        Assert.Equal("anim_79", animation.Name);
        Assert.Empty(animation.Channels); // no skeleton is invented for a model with none
        Assert.Empty(document.Skeletons);

        var channel = Assert.IsType<ModelMorphChannel>(animation.MorphChannel);
        Assert.Equal(12, library.ClipFrameCounts[RuntimeVerifiedClip]);
        Assert.Equal(12, channel.KeyCount); // one key per pose RECORD
        Assert.Equal(11, channel.TargetCount); // frame 0 IS the base, so it adds none
        Assert.Equal(document.Meshes.Count - 1, channel.MeshIndex);

        // Every primitive of the morphing mesh carries the same targets, named by
        // the pose record they came from.
        var mesh = document.Meshes[channel.MeshIndex];
        Assert.Equal(13, mesh.Primitives.Count);
        foreach (var primitive in mesh.Primitives)
        {
            Assert.NotNull(primitive.MorphTargets);
            Assert.Equal(
                Enumerable.Range(1, 11).Select(frame => $"anim_79_f{frame}"),
                primitive.MorphTargets!.Select(static target => target.Name));
            Assert.All(primitive.MorphTargets,
                target => Assert.Equal(primitive.Vertices.Length, target.PositionDeltas.Length));
        }

        // Keys are evenly spaced at the stated export policy rate; the engine's
        // own 2-3 video-frame cadence is deliberately not claimed here.
        Assert.Equal(30f, GbaDhjAnimatedModelWriter.PoseRecordsPerSecond);
        for (var key = 0; key < channel.KeyCount; key++)
            Assert.Equal(key / GbaDhjAnimatedModelWriter.PoseRecordsPerSecond, channel.Times[key]);

        // One target fully applied per key, and key 0 shows the base pose with no
        // target at all.
        Assert.Empty(AppliedTargets(channel, key: 0));
        for (var key = 1; key < channel.KeyCount; key++)
        {
            var applied = Assert.Single(AppliedTargets(channel, key));
            Assert.Equal(key - 1, applied); // this clip never repeats a pose
            Assert.Equal(1f, channel.Weights[key * channel.TargetCount + applied]);
        }
    }

    [CorpusFact]
    public void AnimatedClip_UsesItsOwnFrameZeroAsTheBaseMeshAndReproducesEveryRecord()
    {
        var (rom, model, library) = Load();
        var basePose = GbaDhjModel.ReadPoseFrame(rom, library, RuntimeVerifiedClip, 0);
        var staticDocument = GbaDhjModelGeometryWriter.Build(rom, model, basePose, "rider_19");
        var document = GbaDhjAnimatedModelWriter.TryBuild(
            rom, model, library, RuntimeVerifiedClip, "rider_19")!;

        // The animated base mesh IS the single-pose export of the clip's frame 0:
        // same triangles, same topology, same positions. Only targets and a
        // weights track are added.
        Assert.Equal(staticDocument.TriangleCount, document.TriangleCount);
        Assert.Equal(110, document.TriangleCount);
        Assert.Equal(staticDocument.Materials.Count, document.Materials.Count);
        var expected = staticDocument.Meshes.SelectMany(static mesh => mesh.Primitives).ToList();
        var actual = document.Meshes.SelectMany(static mesh => mesh.Primitives).ToList();
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Indices, actual[i].Indices);
            Assert.Equal(
                expected[i].Vertices.Select(static vertex => vertex.Position),
                actual[i].Vertices.Select(static vertex => vertex.Position));
        }

        // Base plus the key's target reproduces the pose the ROM record assembles
        // through the engine's own 13-part transform — the whole point of the
        // posed-vertex representation. Each corner is checked against the ONE
        // source vertex it came from rather than against the posed set, so a
        // delta redistributed onto the wrong corner cannot pass.
        var vertices = GbaDhjModel.ReadVertices(rom, model);
        var channel = document.Animations[0].MorphChannel!;
        var mesh = document.Meshes[channel.MeshIndex];
        var sourcesByPrimitive = SourceVertexOrder(rom, model);
        Assert.Equal(mesh.Primitives.Count, sourcesByPrimitive.Count);

        for (var key = 0; key < channel.KeyCount; key++)
        {
            var posed = GbaDhjModelGeometryWriter
                .ApplyPose(vertices, model.VertexCounts,
                    GbaDhjModel.ReadPoseFrame(rom, library, RuntimeVerifiedClip, key))
                .Select(GbaDhjModelGeometryWriter.ToGlb)
                .ToArray();
            var applied = AppliedTargets(channel, key).ToArray();
            for (var p = 0; p < mesh.Primitives.Count; p++)
            {
                var primitive = mesh.Primitives[p];
                var sources = sourcesByPrimitive[p];
                Assert.Equal(primitive.Vertices.Length, sources.Count);
                var deltas = applied.Length == 1
                    ? primitive.MorphTargets![applied[0]].PositionDeltas
                    : new Vector3[primitive.Vertices.Length];
                for (var v = 0; v < primitive.Vertices.Length; v++)
                    AssertNear(primitive.Vertices[v].Position + deltas[v], posed[sources[v]]);
            }
        }
    }

    /// <summary>
    ///     The source-vertex index behind each emitted corner, per primitive.
    ///     Clip 79's frame 0 assembles all 110 authored triangles with none
    ///     degenerate, so the writer emits every face's corners in group order and
    ///     this sequence is exact.
    /// </summary>
    private static List<List<int>> SourceVertexOrder(
        ReadOnlySpan<byte> rom, GbaDhjModel.ModelInfo model)
    {
        var order = new List<List<int>>();
        foreach (var group in GbaDhjModel.ReadFaces(rom, model)
                     .GroupBy(static face => face.Group)
                     .OrderBy(static group => group.Key))
        {
            var sources = new List<int>();
            foreach (var face in group)
            {
                sources.Add(face.V0);
                sources.Add(face.V1);
                sources.Add(face.V2);
            }

            order.Add(sources);
        }

        return order;
    }

    [CorpusFact]
    public void PoseRecordThatRepeatsTheBasePose_AddsNoTarget()
    {
        var (rom, model, library) = Load();
        var frameCount = library.ClipFrameCounts[ClipThatReturnsToItsBasePose];
        Assert.Equal(24, frameCount);

        // The ROM really does repeat frame 0 here; this is not a synthetic case.
        var basePose = GbaDhjModel.ReadPoseFrame(rom, library, ClipThatReturnsToItsBasePose, 0);
        var repeat = GbaDhjModel.ReadPoseFrame(
            rom, library, ClipThatReturnsToItsBasePose, FrameRepeatingTheBasePose);
        Assert.Equal(basePose.Parts, repeat.Parts);

        var document = GbaDhjAnimatedModelWriter.TryBuild(
            rom, model, library, ClipThatReturnsToItsBasePose, "rider_19")!;
        var channel = document.Animations[0].MorphChannel!;
        Assert.Equal(24, channel.KeyCount);
        Assert.Equal(22, channel.TargetCount); // 24 records, minus frame 0 and its repeat

        // The repeating key applies nothing at all, which is how a key shows the
        // base pose, and no target is named after it.
        Assert.Empty(AppliedTargets(channel, FrameRepeatingTheBasePose));
        Assert.All(document.Meshes[channel.MeshIndex].Primitives, primitive =>
            Assert.DoesNotContain(
                $"anim_{ClipThatReturnsToItsBasePose}_f{FrameRepeatingTheBasePose}",
                primitive.MorphTargets!.Select(static target => target.Name)));
    }

    /// <summary>
    ///     The directory's last clip states its own length in the u32 prefix the
    ///     offsets point past, exactly as every other clip does, so it reads and
    ///     animates like any other. It used to be refused as unbounded.
    /// </summary>
    [CorpusFact]
    public void FinalClip_AnimatesLikeAnyOther()
    {
        var (rom, model, library) = Load();
        Assert.Equal(FinalClip, library.ClipCount - 1);
        Assert.Equal(25, library.ClipFrameCounts[FinalClip]);

        // Its last record is inside the clip and its 26th is past the end.
        var last = GbaDhjModel.ReadPoseFrame(rom, library, FinalClip, 24);
        Assert.Equal(library.ClipOffsets[FinalClip] + 24 * GbaDhjModel.PoseRecordSize, last.Offset);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GbaDhjModel.ReadPoseFrame(rom, library, FinalClip, 25));

        var document = GbaDhjAnimatedModelWriter.TryBuild(
            rom, model, library, FinalClip, "rider_19");
        Assert.NotNull(document);
        var channel = Assert.Single(document!.Animations).MorphChannel!;
        Assert.Equal($"anim_{FinalClip}", document.Animations[0].Name);
        Assert.Equal(25, channel.KeyCount); // one key per pose RECORD
        Assert.Equal(110, document.TriangleCount);

        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.Equal(110, triangles);
    }

    [CorpusFact]
    public void OutOfRangeAndUndecodedClips_FailClosed()
    {
        var (rom, model, library) = Load();

        foreach (var clip in (int[]) [library.ClipCount, -1])
        {
            // Null, not a quietly degraded single-pose document: a caller must be
            // able to tell that the request was refused.
            Assert.Null(GbaDhjAnimatedModelWriter.TryBuild(rom, model, library, clip, "rider_19"));
        }

        // A library synthesised with no decoded length for a clip still refuses it
        // rather than reading pose records out of whatever follows in ROM.
        var counts = library.ClipFrameCounts.ToArray();
        counts[FinalClip] = 0;
        var undecoded = new GbaDhjModel.PoseLibraryInfo(
            library.HeaderOffset, library.ClipOffsets, counts);
        Assert.Throws<InvalidDataException>(
            () => GbaDhjModel.ReadPoseFrame(rom, undecoded, FinalClip, 0));
        Assert.Null(GbaDhjAnimatedModelWriter.TryBuild(
            rom, model, undecoded, FinalClip, "rider_19"));
    }

    [CorpusFact]
    public void SinglePoseExport_IsUnchangedByTheAnimatedPath()
    {
        var (rom, model, library) = Load();
        var pose = GbaDhjModel.ReadPoseFrame(rom, library, RuntimeVerifiedClip, 0);
        var document = GbaDhjModelGeometryWriter.Build(rom, model, pose, "rider_19");

        Assert.Empty(document.Animations);
        Assert.Equal(110, document.TriangleCount);
        Assert.Equal(13, document.Materials.Count);
        var primitives = document.Meshes.SelectMany(static mesh => mesh.Primitives).ToList();
        Assert.Equal(13, primitives.Count);
        Assert.All(primitives, static primitive => Assert.Null(primitive.MorphTargets));

        // The single-pose route keeps its flat per-face normals: all three corners
        // of a triangle share one normal. Only the animated route swaps in
        // per-vertex normals, which it needs so a morph delta resolves to exactly
        // one (position, normal) pair.
        foreach (var primitive in primitives)
        {
            for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
            {
                var normal = primitive.Vertices[primitive.Indices[i]].Normal;
                Assert.Equal(normal, primitive.Vertices[primitive.Indices[i + 1]].Normal);
                Assert.Equal(normal, primitive.Vertices[primitive.Indices[i + 2]].Normal);
            }
        }
    }

    [CorpusFact]
    public void OneClipExportsAsAMorphGlbWithNoSkin()
    {
        var (rom, model, library) = Load();
        var document = GbaDhjAnimatedModelWriter.TryBuild(
            rom, model, library, RuntimeVerifiedClip, "rider_19")!;

        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.Equal(110, triangles);

        using var stream = new MemoryStream(glb!, writable: false);
        var exported = ModelRoot.ReadGLB(stream);
        var animation = Assert.Single(exported.LogicalAnimations);
        Assert.Equal("anim_79", animation.Name);

        // A weights track and no skin: this rider has no skeleton in the file.
        Assert.Empty(exported.LogicalSkins);
        Assert.All(exported.LogicalMeshes.SelectMany(static mesh => mesh.Primitives), primitive =>
        {
            // The writer keys deltas by base geometry and silently drops a mesh's
            // morphing when two corners disagree, so an emitted count of 11 is
            // also the proof that no two source vertices collided.
            Assert.Equal(11, primitive.MorphTargetsCount);
            Assert.Null(primitive.GetVertexAccessor("JOINTS_0"));
        });
    }

    /// <summary>Weights are written as literal 0 or 1, so a plain comparison is
    ///     exact here.</summary>
    private static IEnumerable<int> AppliedTargets(ModelMorphChannel channel, int key) =>
        Enumerable.Range(0, channel.TargetCount)
            .Where(target => channel.Weights[key * channel.TargetCount + target] > 0f);

    /// <summary>
    ///     Base plus delta reaches the posed vertex by a different order of
    ///     floating-point operations than the pose itself, so it is compared within
    ///     a tolerance far below one model unit rather than bit-exactly.
    /// </summary>
    private static void AssertNear(Vector3 value, Vector3 expected)
    {
        var distance = Vector3.Distance(value, expected);
        Assert.True(distance < 1e-3f, $"{value} is {distance} from the posed vertex {expected}");
    }

    private (byte[] Rom, GbaDhjModel.ModelInfo Model, GbaDhjModel.PoseLibraryInfo Library) Load()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        return (rom, GbaDhjModel.FindModels(rom)[19], GbaDhjModel.FindPoseLibraries(rom)[0]);
    }
}
