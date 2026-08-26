using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds an animated, skinned GBA skater the way the hardware animates it —
///     almost. The engine is a pure morph player: every animation frame stores the
///     complete posed vertex set and the renderer draws whichever frame the clip's
///     tick→frame remap names. glTF has no bone-free way to express that which the
///     rest of this pipeline could carry, so the export synthesises the equivalent
///     rig: <b>one bone per unique model vertex</b> (172 for THPS2), every rendered
///     corner bound to its source vertex's bone at weight 1, and per-tick
///     TRANSLATION-only channels whose keys are the frame's absolute vertex
///     positions. Bind pose is frame 0, so bind geometry equals the static export
///     by construction (the DS <see cref="NdsAnimatedModelWriter" /> discipline).
///
///     <para>The 3 per-frame anchor bytes are the pose's AABB centre (measured
///     200/200 sampled frames) — a render/cull pivot, deliberately NOT applied as
///     translation: the vertices already carry the full motion, and adding the
///     anchor would double it.</para>
///
///     <para>Fail-closed: an empty, out-of-range, or duplicate clip selection
///     contributes nothing, and if no clip survives the document is left untouched
///     for the caller's plain static path — an invalid selection never alters the
///     geometry that would have been exported without it.</para>
///
///     <para><see cref="TicksPerSecond" /> is an explicit export policy, not a
///     measured runtime property: GBA video runs 59.7275 Hz and the clip tick is
///     assumed to advance once per hardware frame; 60 keeps key times exact.</para>
/// </summary>
internal static class GbaAnimatedModelWriter
{
    public const float TicksPerSecond = 60f;

    /// <summary>
    ///     Populates geometry, skeleton, skin and the selected clips. Returns the
    ///     number of clips exported; zero means nothing was applicable and NOTHING
    ///     was added — the caller should populate the static document instead.
    /// </summary>
    public static int TryPopulate(
        ModelDocument document,
        GbaModelNativeSource native,
        IReadOnlyList<int>? clipIndices,
        bool includeAllClips)
    {
        var rom = native.Rom;
        var model = GbaSkaterModel.TryLocate(rom);
        if (model == null)
            return 0;

        var clips = GbaSkaterModel.ReadClips(rom, model);
        var selected = includeAllClips
            ? Enumerable.Range(0, clips.Count)
            : (clipIndices ?? []).Distinct();

        // One bone per unique model vertex; corner influences resolve through the
        // same sub-object bases.
        var boneBase = new int[GbaSkaterModel.SubObjectCount];
        var boneCount = 0;
        for (var sub = 0; sub < GbaSkaterModel.SubObjectCount; sub++)
        {
            boneBase[sub] = boneCount;
            boneCount += model.VertCounts[sub];
        }

        // The cart embeds the same tricks.bin the PS1 discs do, so a clip a
        // single trick owns gets its real name here as well as in the pane.
        var trickNames = GbaTricksFile.TryBuildClipNames(rom, clips.Count);

        var skeletonIndex = document.Skeletons.Count;
        var frameCache = new Dictionary<int, Vector3[]>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var animations = new List<ModelAnimation>();
        foreach (var index in selected)
        {
            if (index < 0 || index >= clips.Count || clips[index].TickCount == 0)
                continue;
            var label = trickNames != null && trickNames.TryGetValue(index, out var trick)
                ? trick
                : $"anim_{index}";
            var name = AnimationExportName.ForMesh(meshStem: string.Empty, label, usedNames);
            animations.Add(BakeClip(name, rom, model, clips[index], boneCount,
                boneBase, skeletonIndex, frameCache));
        }

        if (animations.Count == 0)
            return 0;

        // Flat rig at the frame-0 pose. Pure translations, so the inverse bind is
        // constructed directly rather than through Matrix4x4.Invert — bind identity
        // (T(p0)·T(-p0) = I) is then exact and bind geometry equals the static export.
        var bindPose = PoseOf(rom, model, frame: 0, boneCount, frameCache);
        var skeleton = new ModelSkeleton { Name = "skeleton" };
        for (var b = 0; b < boneCount; b++)
        {
            skeleton.Bones.Add(new ModelBone
            {
                Name = $"joint_{b:D3}",
                LocalTransform = Matrix4x4.CreateTranslation(bindPose[b]),
                InverseBindMatrix = Matrix4x4.CreateTranslation(-bindPose[b])
            });
        }

        document.Skeletons.Add(skeleton);
        GbaModelGeometryWriter.Populate(document, native, new GbaSkinAssignment
        {
            SkeletonIndex = skeletonIndex,
            SubObjectBoneBase = boneBase
        });
        document.Animations.AddRange(animations);
        return animations.Count;
    }

    /// <summary>
    ///     One key per tick, the tick→frame remap honoured tick-by-tick (73 of the
    ///     217 clips hold or reorder frames — a frame range would misplay them).
    ///     One shared times[] instance serves all bones' channels.
    /// </summary>
    private static ModelAnimation BakeClip(
        string name, ReadOnlySpan<byte> rom, GbaSkaterModel.ModelInfo model,
        GbaSkaterModel.Clip clip, int boneCount, int[] boneBase, int skeletonIndex,
        Dictionary<int, Vector3[]> frameCache)
    {
        var frames = GbaSkaterModel.ClipFrames(rom, model, clip);
        var times = new float[frames.Length];
        var translations = new float[boneCount][];
        for (var b = 0; b < boneCount; b++)
            translations[b] = new float[frames.Length * 3];

        for (var t = 0; t < frames.Length; t++)
        {
            times[t] = t / TicksPerSecond;
            var pose = PoseOf(rom, model, frames[t], boneCount, frameCache);
            for (var b = 0; b < boneCount; b++)
            {
                translations[b][t * 3] = pose[b].X;
                translations[b][t * 3 + 1] = pose[b].Y;
                translations[b][t * 3 + 2] = pose[b].Z;
            }
        }

        var animation = new ModelAnimation { Name = name };
        for (var b = 0; b < boneCount; b++)
        {
            animation.Channels.Add(new ModelAnimationChannel
            {
                SkeletonIndex = skeletonIndex,
                BoneIndex = b,
                Property = ModelAnimationProperty.Translation,
                Times = times,
                Values = translations[b]
            });
        }

        return animation;
    }

    /// <summary>A frame's 172 vertex positions in GLB space, flattened in bone
    ///     order (sub-objects consecutive). Cached: 7,874 ticks reference at most
    ///     4,772 distinct frames, and holds repeat frames within one clip.</summary>
    private static Vector3[] PoseOf(
        ReadOnlySpan<byte> rom, GbaSkaterModel.ModelInfo model, int frame,
        int boneCount, Dictionary<int, Vector3[]> frameCache)
    {
        if (frameCache.TryGetValue(frame, out var cached))
            return cached;

        var verts = GbaSkaterModel.ReadFrameVertices(rom, model, frame);
        var pose = new Vector3[boneCount];
        var b = 0;
        for (var sub = 0; sub < GbaSkaterModel.SubObjectCount; sub++)
        {
            foreach (var v in verts[sub])
                pose[b++] = GbaModelGeometryWriter.ToGlb(v);
        }

        frameCache[frame] = pose;
        return pose;
    }
}
