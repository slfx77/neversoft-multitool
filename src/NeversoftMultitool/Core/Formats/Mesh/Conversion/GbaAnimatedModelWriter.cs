using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Exports one THPS2 GBA skater clip the way the engine actually animates:
///     as <b>morph targets</b>. The GBA has no skeleton — every animation frame
///     stores the complete posed vertex set and the renderer draws whichever
///     frame the clip's tick→frame remap names — and glTF expresses exactly that,
///     so the clip's distinct frames become the mesh's targets and the tick
///     sequence becomes a weights track that selects one of them per tick.
///
///     <para>The base mesh is the pool's frame 0 (the same neutral pose the
///     static export writes), so a target is the plain difference from it.</para>
///
///     <para><b>One clip per document.</b> A glTF weights track carries one value
///     per target per key, so bundling all 217 clips would mean 4,772 targets and
///     a weights array in the gigabytes; per clip it is ~22 targets and a few
///     kilobytes. Callers wanting several clips export several files.</para>
///
///     <para>The 3 per-frame anchor bytes are the pose's AABB centre (measured
///     200/200 sampled frames) — a render/cull pivot, deliberately NOT applied:
///     the vertices already carry the full motion.</para>
///
///     <para><see cref="TicksPerSecond" /> is an explicit export policy, not a
///     measured runtime property: GBA video runs 59.7275 Hz and the clip tick is
///     assumed to advance once per hardware frame; 60 keeps key times exact.</para>
/// </summary>
internal static class GbaAnimatedModelWriter
{
    public const float TicksPerSecond = 60f;

    /// <summary>
    ///     Populates geometry plus one clip's morph targets and weights. Returns
    ///     false when the clip cannot be exported, having added NOTHING — the
    ///     caller then populates the plain static document instead.
    /// </summary>
    public static bool TryPopulate(
        ModelDocument document, GbaModelNativeSource native, int clipIndex, string? clipName = null)
    {
        var rom = native.Rom;
        var model = GbaSkaterModel.TryLocate(rom);
        if (model == null)
            return false;

        var clips = GbaSkaterModel.ReadClips(rom, model);
        if (clipIndex < 0 || clipIndex >= clips.Count || clips[clipIndex].TickCount == 0)
            return false;

        var ticks = GbaSkaterModel.ClipFrames(rom, model, clips[clipIndex]);
        var basePose = PoseOf(rom, model, frame: 0);

        // Targets are keyed by POSE, not by frame: a hold reuses one target
        // (73 of the 217 clips hold or reorder), and a frame whose pose IS the
        // base contributes none — an all-zero target would be dropped on write
        // and silently shift every later target's index. So each target is
        // guaranteed distinct from the base and from its siblings.
        var targetOfFrame = new Dictionary<int, int>();
        var targetOfPose = new Dictionary<string, int>(StringComparer.Ordinal);
        var frames = new List<int>();
        var deltas = new List<Vector3[]>();
        foreach (var frame in ticks.Distinct())
        {
            var delta = Subtract(PoseOf(rom, model, frame), basePose);
            if (delta.All(static d => d == Vector3.Zero))
                continue; // this frame IS the base pose
            var key = PoseKey(delta);
            if (!targetOfPose.TryGetValue(key, out var target))
            {
                target = frames.Count;
                targetOfPose[key] = target;
                frames.Add(frame);
                deltas.Add(delta);
            }

            targetOfFrame[frame] = target;
        }

        var name = clipName ?? GbaTrickClipName(rom, model, clips.Count, clipIndex);
        GbaModelGeometryWriter.Populate(document, native,
            morphTargets: frames.Count == 0
                ? null
                : new GbaMorphTargets([.. frames], [.. deltas], name));

        // A clip that never leaves the base pose has no motion to express: the
        // document is the static model in that pose, with no animation.
        if (frames.Count == 0)
            return true;

        // One target fully applied per tick (weights all zero shows the base):
        // the engine draws a discrete pose per tick, and interpolating between
        // adjacent keys only smooths playback slowed below the authored rate.
        var times = new float[ticks.Length];
        var weights = new float[ticks.Length * frames.Count];
        for (var tick = 0; tick < ticks.Length; tick++)
        {
            times[tick] = tick / TicksPerSecond;
            if (targetOfFrame.TryGetValue(ticks[tick], out var target))
                weights[tick * frames.Count + target] = 1f;
        }

        document.Animations.Add(new ModelAnimation
        {
            Name = name,
            MorphChannel = new ModelMorphChannel
            {
                MeshIndex = document.Meshes.Count - 1,
                TargetCount = frames.Count,
                Times = times,
                Weights = weights
            }
        });
        return true;
    }

    /// <summary>The clip's exported name: the cart's own trick name where one
    ///     trick uniquely owns the clip, else the synthetic label.</summary>
    public static string GbaTrickClipName(
        ReadOnlySpan<byte> rom, GbaSkaterModel.ModelInfo model, int clipCount, int clipIndex)
    {
        var names = GbaTricksFile.TryBuildClipNames(rom, clipCount);
        return names != null && names.TryGetValue(clipIndex, out var trick)
            ? AnimationExportName.ForMesh(meshStem: string.Empty, trick)
            : $"anim_{clipIndex}";
    }

    private static Vector3[] PoseOf(ReadOnlySpan<byte> rom, GbaSkaterModel.ModelInfo model, int frame)
    {
        var verts = GbaSkaterModel.ReadFrameVertices(rom, model, frame);
        var pose = new List<Vector3>(model.VertCounts.Sum(count => count));
        for (var sub = 0; sub < GbaSkaterModel.SubObjectCount; sub++)
            pose.AddRange(verts[sub].Select(GbaModelGeometryWriter.ToGlb));
        return [.. pose];
    }

    /// <summary>Exact identity of a pose delta. Coordinates are s8 scaled by a
    ///     constant, so equality is exact — no tolerance is involved.</summary>
    private static string PoseKey(Vector3[] delta)
    {
        return string.Create(null, $"{string.Join(',', delta.Select(static d => $"{d.X},{d.Y},{d.Z}"))}");
    }

    private static Vector3[] Subtract(Vector3[] pose, Vector3[] basePose)
    {
        var delta = new Vector3[pose.Length];
        for (var i = 0; i < pose.Length; i++)
            delta[i] = pose[i] - basePose[i];
        return delta;
    }
}

/// <summary>
///     One clip's morph targets in SOURCE-VERTEX order, for the geometry writer
///     to redistribute onto each primitive's own corners.
/// </summary>
internal sealed record GbaMorphTargets(int[] Frames, Vector3[][] DeltasByTarget, string ClipName)
{
    /// <summary>
    ///     Redistributes source-vertex deltas onto one primitive's own corners.
    ///     <paramref name="sources" /> is the source-vertex index behind each of
    ///     the primitive's emitted corners, in the same order.
    /// </summary>
    internal ModelMorphTarget[] ForPrimitive(IReadOnlyList<int> sources)
    {
        var targets = new ModelMorphTarget[DeltasByTarget.Length];
        for (var t = 0; t < targets.Length; t++)
        {
            var source = DeltasByTarget[t];
            var deltas = new Vector3[sources.Count];
            for (var v = 0; v < sources.Count; v++)
                deltas[v] = source[sources[v]];
            targets[t] = new ModelMorphTarget
            {
                Name = $"{ClipName}_f{Frames[t]}",
                PositionDeltas = deltas
            };
        }

        return targets;
    }
}
