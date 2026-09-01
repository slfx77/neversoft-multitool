using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Exports one Downhill Jam GBA pose clip as glTF <b>morph targets</b>.
///
///     <para>DHJ's rider has no skeleton in the file: its vertices are authored in
///     13 independent rigid-part spaces and a <c>0x50</c>-byte pose record supplies
///     each part's own translation and three Euler bytes, which the engine's
///     transform (ROM <c>0x080009DC</c>, copied to IWRAM <c>0x030045BC</c>) applies
///     before any triangle is drawn. Posing every record of a clip and storing the
///     complete resulting vertex sets therefore reproduces the assembly the
///     renderer performs, and glTF expresses exactly that as morph targets — one
///     target per distinct posed vertex set, plus a weights track that selects one
///     of them per key. (As for the single-pose export, the assembly is faithful
///     rather than bit-exact: the writer uses floating-point trigonometry where the
///     engine uses its 512-scale integer sine table.)</para>
///
///     <para>Morph targets are deliberately preferred over a one-joint-per-part
///     skin for this first milestone. Part membership is known, so a skin is
///     possible, but the stored Euler bytes cannot be converted to joint rotations
///     naively: the engine's Y stage reflects Z and the export space reflects again,
///     and the two cancel only across the complete matrix. Posed vertices sidestep
///     that entirely, and they also keep the faces that connect vertices from
///     different parts (DHJ face indices address the whole transformed array, not a
///     per-part run).</para>
///
///     <para>The base mesh is the clip's own frame 0, so a target is the plain
///     difference from it and the animated document's geometry is the single-pose
///     export of that same frame.</para>
///
///     <para><b>One clip per document</b>, as for the THPS2 cart: a glTF weights
///     track carries one value per target per key, so the directory's clips are
///     exported one file at a time rather than as one combined document.</para>
///
///     <para><see cref="PoseRecordsPerSecond" /> is an explicit EXPORT POLICY, not
///     a measured runtime property — see the remarks below.</para>
/// </summary>
/// <remarks>
///     <para><b>Why one key per pose record, and why 30.</b> The retained gameplay
///     trace (<c>TestOutput/gba-dhj-runtime-trace.txt</c>, clip 90) shows the
///     descriptor's pose pointer at EWRAM <c>0x02036B90</c> advancing by one
///     <c>0x50</c> record at video frames 4560, 4562, 4564, 4567, … — that is one
///     record every two to three video frames, not one per frame. So a 59.7275 Hz
///     "one record per hardware tick" reading, the assumption the THPS2 exporter
///     can make about its own tick remap, would be wrong here.</para>
///
///     <para>What the trace does establish is the record ORDER and that records are
///     consumed one at a time, so a key per pose record is a faithful
///     representation; only its spacing is unproven. The exporter therefore emits
///     evenly spaced keys and states the rate as policy. 30 Hz is the fastest
///     cadence actually observed (one record per two video frames, 59.7275/2 ≈
///     29.86 Hz, rounded to 30 so key times stay exact), which makes the exported
///     clip an upper bound on playback speed rather than an invented number. The
///     three-frame gaps in the same trace mean real playback is slower and possibly
///     variable or state-driven; nothing here claims otherwise, and neither the
///     per-clip <c>u32</c> trailer nor a loop/transition rule has been decoded.</para>
/// </remarks>
internal static class GbaDhjAnimatedModelWriter
{
    /// <summary>Export policy — see the remarks on <see cref="GbaDhjAnimatedModelWriter" />.</summary>
    public const float PoseRecordsPerSecond = 30f;

    /// <summary>
    ///     Builds the rider posed at <paramref name="clipIndex" />'s frame 0, with
    ///     the clip's remaining pose records as morph targets and a weights track.
    ///
    ///     <para>Returns null — having built NOTHING — when the clip cannot be
    ///     animated: an out-of-range index, or the directory's final clip, whose
    ///     frame count <see cref="GbaDhjModel.FindPoseLibraries" /> leaves at
    ///     <c>-1</c> because no following offset bounds it. Callers must surface
    ///     that as an error: answering an animation request with a silent
    ///     single-pose export would misrepresent an unbounded clip as a decoded
    ///     one.</para>
    /// </summary>
    public static ModelDocument? TryBuild(
        ReadOnlySpan<byte> rom,
        GbaDhjModel.ModelInfo model,
        GbaDhjModel.PoseLibraryInfo library,
        int clipIndex,
        string name)
    {
        if ((uint)clipIndex >= (uint)library.ClipCount)
            return null;

        var frameCount = library.ClipFrameCounts[clipIndex];
        if (frameCount < 1)
            return null;

        var vertices = GbaDhjModel.ReadVertices(rom, model);
        var basePose = GbaDhjModel.ReadPoseFrame(rom, library, clipIndex, 0);
        var baseVertices = ToGlb(
            GbaDhjModelGeometryWriter.ApplyPose(vertices, model.VertexCounts, basePose));

        // Targets are keyed by POSE, not by frame: a clip that holds or returns to
        // a pose reuses one target, and a frame whose pose IS the base contributes
        // none — an all-zero target is dropped when the glTF is written and would
        // silently shift every later target's index. Each target is therefore
        // distinct from the base and from its siblings.
        var targetOfFrame = new Dictionary<int, int>();
        var targetOfPose = new Dictionary<string, int>(StringComparer.Ordinal);
        var frames = new List<int>();
        var deltas = new List<Vector3[]>();
        for (var frame = 0; frame < frameCount; frame++)
        {
            var posed = ToGlb(GbaDhjModelGeometryWriter.ApplyPose(
                vertices,
                model.VertexCounts,
                GbaDhjModel.ReadPoseFrame(rom, library, clipIndex, frame)));
            var delta = Subtract(posed, baseVertices);
            if (delta.All(static d => d == Vector3.Zero))
                continue; // this record poses the rider exactly as the base does

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

        var clipName = ClipName(clipIndex);
        var document = GbaDhjModelGeometryWriter.Build(
            rom, model, basePose, name,
            morphTargets: frames.Count == 0
                ? null
                : new GbaMorphTargets([.. frames], [.. deltas], clipName));

        // A clip that never leaves its own frame 0 has no motion to express: the
        // document is the rider in that pose, with no animation.
        if (frames.Count == 0 || document.Meshes.Count == 0)
            return document;

        // One target fully applied per key, with all-zero weights showing the base
        // pose. The traced render path reads a single pose record and transforms
        // the parts from it, so a key is a whole authored pose; blending between
        // adjacent keys is the viewer's own smoothing of playback slowed below the
        // exported rate, not something claimed about the engine.
        var times = new float[frameCount];
        var weights = new float[frameCount * frames.Count];
        for (var frame = 0; frame < frameCount; frame++)
        {
            times[frame] = frame / PoseRecordsPerSecond;
            if (targetOfFrame.TryGetValue(frame, out var target))
                weights[frame * frames.Count + target] = 1f;
        }

        document.Animations.Add(new ModelAnimation
        {
            Name = clipName,
            MorphChannel = new ModelMorphChannel
            {
                MeshIndex = document.Meshes.Count - 1,
                TargetCount = frames.Count,
                Times = times,
                Weights = weights
            }
        });
        return document;
    }

    /// <summary>
    ///     The clip's exported name. DHJ's pose directory carries no names — its
    ///     clips are addressed purely by index — so this label is synthetic and is
    ///     spelled to say so.
    /// </summary>
    public static string ClipName(int clipIndex) => $"anim_{clipIndex}";

    private static Vector3[] ToGlb(Vector3[] posedVertices)
    {
        var result = new Vector3[posedVertices.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = GbaDhjModelGeometryWriter.ToGlb(posedVertices[i]);
        return result;
    }

    private static Vector3[] Subtract(Vector3[] pose, Vector3[] basePose)
    {
        var delta = new Vector3[pose.Length];
        for (var i = 0; i < pose.Length; i++)
            delta[i] = pose[i] - basePose[i];
        return delta;
    }

    /// <summary>
    ///     Exact identity of a pose delta. The pose transform is deterministic, so
    ///     two records with the same bytes produce bit-identical vertices and this
    ///     round-trippable spelling compares them exactly — no tolerance is
    ///     involved, and two records that merely look similar stay separate targets.
    /// </summary>
    private static string PoseKey(Vector3[] delta) =>
        string.Join(';', delta.Select(static d => $"{d.X},{d.Y},{d.Z}"));
}
