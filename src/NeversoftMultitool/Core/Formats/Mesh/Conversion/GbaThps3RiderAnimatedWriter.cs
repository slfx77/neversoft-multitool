using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Exports one THPS3 GBA rider clip the way the engine animates it: as
///     <b>morph targets</b>. Every pose frame stores the rider's complete vertex
///     set and the deck's translation, the clip's tick→frame remap names one
///     frame per tick, and glTF expresses exactly that — the clip's distinct
///     poses become the mesh's targets and the tick sequence a weights track.
///     The base mesh is the pool's frame 0, so a target is the plain difference
///     from it; the deck moves by the difference of its per-frame translation.
///
///     <para><b>One clip per document</b> (a weights track addresses every
///     target of the mesh), the same contract as the THPS2 skater. Clips are
///     anonymous: THPS3 embeds no tricks.bin dispatcher, so names are
///     <c>anim_N</c>.</para>
///
///     <para><see cref="TicksPerSecond" /> is an explicit export policy, not a
///     measured runtime property: the remap is read at one tick per hardware
///     frame (59.7275 Hz, kept exact as 60). The remap holds every frame for two
///     consecutive ticks throughout, so the authored cadence is 30 poses a second;
///     no loop or transition rule is decoded and none is emitted.</para>
/// </summary>
internal static class GbaThps3RiderAnimatedWriter
{
    public const float TicksPerSecond = 60f;

    /// <summary>
    ///     Populates geometry plus one clip's morph targets and weights. Returns
    ///     false when the clip cannot be exported, having added NOTHING — the
    ///     caller then populates the plain static document instead.
    /// </summary>
    public static bool TryPopulate(ModelDocument document, GbaThps3RiderNativeSource native, int clipIndex)
    {
        var rom = native.Rom;
        var model = GbaThps3RiderModel.TryLocate(rom);
        if (model == null)
            return false;

        var clips = GbaThps3RiderModel.ReadClips(rom, model);
        if (clipIndex < 0 || clipIndex >= clips.Count || clips[clipIndex].TickCount == 0)
            return false;

        var ticks = GbaThps3RiderModel.ClipFrames(rom, model, clips[clipIndex]);
        var basePose = GbaThps3RiderGeometryWriter.PoseOf(rom, model, frame: 0);

        // Targets are keyed by POSE, not by frame: a hold reuses one target, and
        // a frame whose pose IS the base contributes none — an all-zero target
        // would be dropped on write and silently shift every later target's index.
        var targetOfFrame = new Dictionary<int, int>();
        var targetOfPose = new Dictionary<string, int>(StringComparer.Ordinal);
        var frames = new List<int>();
        var deltas = new List<Vector3[]>();
        foreach (var frame in ticks.Distinct())
        {
            var pose = GbaThps3RiderGeometryWriter.PoseOf(rom, model, frame);
            var delta = new Vector3[pose.Length];
            var moved = false;
            for (var i = 0; i < pose.Length; i++)
            {
                delta[i] = pose[i] - basePose[i];
                moved |= delta[i] != Vector3.Zero;
            }

            if (!moved)
                continue; // this frame IS the base pose
            var key = string.Join(';', delta.Select(static d => $"{d.X},{d.Y},{d.Z}"));
            if (!targetOfPose.TryGetValue(key, out var target))
            {
                target = frames.Count;
                targetOfPose[key] = target;
                frames.Add(frame);
                deltas.Add(delta);
            }

            targetOfFrame[frame] = target;
        }

        var name = ClipName(clipIndex);
        GbaThps3RiderGeometryWriter.Populate(document, native,
            morphTargets: frames.Count == 0 ? null : new GbaMorphTargets([.. frames], [.. deltas], name));

        // A clip that never leaves the base pose has no motion to express.
        if (frames.Count == 0)
            return true;

        // One target fully applied per tick: the engine draws a discrete pose per
        // tick, and interpolating between keys would only smooth slowed playback.
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

    public static string ClipName(int clipIndex) => $"anim_{clipIndex}";
}
