using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds an animated, skinned DS model the way the hardware animates it: each
///     frame is produced by scattering the clip's pose into a copy of the display
///     list (<see cref="NdsPoseScatter" />) and re-running the interpreter, and each
///     "bone" is one of the matrices the list transformed vertices with, identified
///     by provenance. The engine has no runtime skeleton — the hierarchy lives in
///     the list's PUSH/MULT/POP nesting — so the bones here are flat, carrying their
///     measured GLOBAL transform per frame, which is exact by construction.
///
///     Fail-closed: a clip that cannot be applied (channel counts disagreeing with
///     the geometry's flag census), fails to decompose, or changes the set of
///     vertex-transforming matrices is skipped whole. If no clip survives, the
///     caller keeps the plain static document — an invalid selection never alters
///     the geometry that would have been exported without it.
///
///     The 30 fps cadence is an explicit export policy, not a measured runtime
///     property of the DS engine.
/// </summary>
internal static class NdsAnimatedModelWriter
{
    public const float FramesPerSecond = 30f;

    /// <summary>
    ///     Rotates Z-up native space into glTF's Y-up, matching the vertex
    ///     conversion in <see cref="NdsGeometryWriter" />: (x, y, z) -> (x, z, -y).
    /// </summary>
    private static readonly Matrix4x4 Basis = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    private static readonly Matrix4x4 BasisInverse = Matrix4x4.Transpose(Basis);

    /// <summary>
    ///     Populates geometry, skeleton, skin and animations. Returns the number of
    ///     clips exported; zero means nothing was applicable and NOTHING was added —
    ///     the caller should populate the static document instead.
    /// </summary>
    public static int TryPopulate(
        ModelDocument document,
        ReadOnlySpan<byte> data,
        NdsGeometryFile file,
        IReadOnlyList<(string Name, NdsAnimationFile Clip)> clips,
        NdsTextureSource? textures)
    {
        var applicable = clips.Where(c => NdsPoseScatter.CanApply(file, c.Clip)).ToList();
        if (applicable.Count == 0)
            return 0;

        var bind = NdsGxInterpreter.RunInterpreter(data, file);
        if (bind.UsedMatrices.Count == 0)
            return 0;

        // Bones in deterministic order; provenance -1 (the initial identity) too,
        // so unposed vertices still bind to a real joint.
        var provenances = bind.UsedMatrices.Keys.Order().ToArray();
        var boneOf = new Dictionary<int, int>(provenances.Length);
        for (var i = 0; i < provenances.Length; i++)
            boneOf[provenances[i]] = i;

        var skeletonIndex = document.Skeletons.Count;
        var animations = new List<ModelAnimation>();
        foreach (var (name, clip) in applicable)
        {
            var animation = BakeClip(name, data, file, clip, provenances, skeletonIndex);
            if (animation != null)
                animations.Add(animation);
        }

        if (animations.Count == 0)
            return 0;

        var skeleton = new ModelSkeleton { Name = "skeleton" };
        foreach (var provenance in provenances)
        {
            var global = Convert(bind.UsedMatrices[provenance]);
            Matrix4x4.Invert(global, out var inverse);
            skeleton.Bones.Add(new ModelBone
            {
                Name = $"joint_{skeleton.Bones.Count:D3}",
                LocalTransform = global,
                InverseBindMatrix = inverse
            });
        }

        document.Skeletons.Add(skeleton);
        NdsGeometryWriter.PopulateNdsGeometry(document, file, bind.Groups, textures,
            new NdsSkinAssignment
            {
                SkeletonIndex = skeletonIndex,
                BoneByProvenance = boneOf
            });
        document.Animations.AddRange(animations);
        return animations.Count;
    }

    /// <summary>
    ///     Samples every frame of one clip by patch-and-re-run, capturing each
    ///     bone's measured global transform. Null when any frame fails.
    /// </summary>
    private static ModelAnimation? BakeClip(
        string name, ReadOnlySpan<byte> data, NdsGeometryFile file,
        NdsAnimationFile clip, int[] provenances, int skeletonIndex)
    {
        var frames = clip.Frames;
        var times = new float[frames];
        var translations = new float[provenances.Length][];
        var rotations = new float[provenances.Length][];
        var scales = new float[provenances.Length][];
        for (var b = 0; b < provenances.Length; b++)
        {
            translations[b] = new float[frames * 3];
            rotations[b] = new float[frames * 4];
            scales[b] = new float[frames * 3];
        }

        var previous = new Quaternion[provenances.Length];
        for (var f = 0; f < frames; f++)
        {
            times[f] = f / FramesPerSecond;
            var patched = NdsPoseScatter.Apply(data, file, clip, f);
            var posed = NdsGxInterpreter.RunInterpreter(patched, file);

            for (var b = 0; b < provenances.Length; b++)
            {
                if (!posed.UsedMatrices.TryGetValue(provenances[b], out var native))
                    return null;
                var global = Convert(native);
                if (!Matrix4x4.Decompose(global, out var s, out var r, out var t))
                    return null;

                // glTF SLERPs the short way; keep successive keys on one hemisphere
                // or an interpolated playhead snaps at sign flips.
                if (f > 0 && Quaternion.Dot(r, previous[b]) < 0)
                    r = new Quaternion(-r.X, -r.Y, -r.Z, -r.W);
                previous[b] = r;

                translations[b][f * 3] = t.X;
                translations[b][f * 3 + 1] = t.Y;
                translations[b][f * 3 + 2] = t.Z;
                rotations[b][f * 4] = r.X;
                rotations[b][f * 4 + 1] = r.Y;
                rotations[b][f * 4 + 2] = r.Z;
                rotations[b][f * 4 + 3] = r.W;
                scales[b][f * 3] = s.X;
                scales[b][f * 3 + 1] = s.Y;
                scales[b][f * 3 + 2] = s.Z;
            }
        }

        var animation = new ModelAnimation { Name = name };
        for (var b = 0; b < provenances.Length; b++)
        {
            animation.Channels.Add(new ModelAnimationChannel
            {
                SkeletonIndex = skeletonIndex,
                BoneIndex = b,
                Property = ModelAnimationProperty.Translation,
                Times = times,
                Values = translations[b]
            });
            animation.Channels.Add(new ModelAnimationChannel
            {
                SkeletonIndex = skeletonIndex,
                BoneIndex = b,
                Property = ModelAnimationProperty.Rotation,
                Times = times,
                Values = rotations[b]
            });
            animation.Channels.Add(new ModelAnimationChannel
            {
                SkeletonIndex = skeletonIndex,
                BoneIndex = b,
                Property = ModelAnimationProperty.Scale,
                Times = times,
                Values = scales[b]
            });
        }

        return animation;
    }

    /// <summary>Conjugates a native (Z-up) transform into glTF's Y-up space.</summary>
    private static Matrix4x4 Convert(in Matrix4x4 native)
    {
        return BasisInverse * native * Basis;
    }
}
