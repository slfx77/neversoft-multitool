using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Evaluates a <see cref="SkaAnimation" /> against a <see cref="Ps2Skeleton" /> at a
///     given time, producing per-bone local rotation/translation plus the composed
///     world matrix (parent-chain multiply).
///     Version-specific fallback semantics for empty tracks:
///     - Version 1 (THPS4): empty track → identity (the native V1 .ske file carries
///     no bind pose; the engine overlays a default animation at load time, so
///     "no animation track" for V1 means "no transform").
///     - Version 2 (THUG+): empty track → bone's bind pose from the skeleton's
///     LocalRotation/LocalTranslation fields.
///     Single-key tracks are treated as constant. Multi-key tracks LERP translation
///     and SLERP rotation between bracketing keys. Times outside [0, Duration] wrap
///     modulo Duration so looping playback returns a stable pose.
/// </summary>
internal sealed class SkaPoseEvaluator
{
    private readonly SkaAnimation _animation;
    private readonly Ps2Skeleton _skeleton;
    private readonly SkaBoneTrack?[] _tracksByBoneIndex;

    public SkaPoseEvaluator(SkaAnimation animation, Ps2Skeleton skeleton)
    {
        _animation = animation ?? throw new ArgumentNullException(nameof(animation));
        _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));

        // Index bone tracks by BoneIndex for fast lookup. Bones with no corresponding
        // track entry receive null and fall back to version-specific semantics.
        _tracksByBoneIndex = new SkaBoneTrack?[skeleton.Bones.Length];
        foreach (var track in animation.BoneTracks)
        {
            if (track.BoneIndex >= 0 && track.BoneIndex < _tracksByBoneIndex.Length)
                _tracksByBoneIndex[track.BoneIndex] = track;
        }
    }

    /// <summary>
    ///     Sample one <see cref="SkaBonePose" /> per bone in the skeleton at the
    ///     given time. Time wraps modulo <see cref="SkaAnimation.Duration" /> for
    ///     looping animations.
    /// </summary>
    public SkaBonePose[] Evaluate(float time)
    {
        var wrapped = WrapTime(time, _animation.Duration);
        var poses = new SkaBonePose[_skeleton.Bones.Length];

        // Pass 1: resolve each bone's local rotation + translation.
        var localRotations = new Quaternion[_skeleton.Bones.Length];
        var localTranslations = new Vector3[_skeleton.Bones.Length];
        for (var i = 0; i < _skeleton.Bones.Length; i++)
        {
            var bone = _skeleton.Bones[i];
            var track = _tracksByBoneIndex[i];
            localRotations[i] = EvaluateRotation(track, wrapped, bone);
            localTranslations[i] = EvaluateTranslation(track, wrapped, bone);
        }

        // Pass 2: compose parent-chain world matrices. Bones are stored parent-before-child,
        // so a single forward pass is sufficient.
        var worldMatrices = new Matrix4x4[_skeleton.Bones.Length];
        for (var i = 0; i < _skeleton.Bones.Length; i++)
        {
            var local = Matrix4x4.CreateFromQuaternion(localRotations[i]) *
                        Matrix4x4.CreateTranslation(localTranslations[i]);
            var parentIndex = _skeleton.Bones[i].ParentIndex;
            worldMatrices[i] = parentIndex >= 0 && parentIndex < i
                ? local * worldMatrices[parentIndex]
                : local;
            poses[i] = new SkaBonePose(localRotations[i], localTranslations[i], worldMatrices[i]);
        }

        return poses;
    }

    private Quaternion EvaluateRotation(SkaBoneTrack? track, float time, Ps2Bone bone)
    {
        // SKA encodes "no animation for this bone" either as an empty track or as a
        // single identity-quaternion placeholder. Both funnel into the version-specific
        // fallback: V1 skeletons (THPS4) have no native bind pose, so fall through to
        // identity; V2+ skeletons use the bone's stored LocalRotation.
        if (track == null
            || track.RotationKeys.Length == 0
            || (track.RotationKeys.Length == 1 && track.RotationKeys[0].Rotation == Quaternion.Identity))
        {
            return _skeleton.Version == 1 ? Quaternion.Identity : bone.LocalRotation;
        }

        var keys = track.RotationKeys;
        if (keys.Length == 1)
            return keys[0].Rotation;

        var (a, b, t) = FindBracket(keys, time, k => k.Time);
        return Quaternion.Normalize(Quaternion.Slerp(keys[a].Rotation, keys[b].Rotation, t));
    }

    private Vector3 EvaluateTranslation(SkaBoneTrack? track, float time, Ps2Bone bone)
    {
        if (track == null
            || track.TranslationKeys.Length == 0
            || (track.TranslationKeys.Length == 1 && track.TranslationKeys[0].Translation == Vector3.Zero))
        {
            return _skeleton.Version == 1 ? Vector3.Zero : bone.LocalTranslation;
        }

        var keys = track.TranslationKeys;
        if (keys.Length == 1)
            return keys[0].Translation;

        var (a, b, t) = FindBracket(keys, time, k => k.Time);
        return Vector3.Lerp(keys[a].Translation, keys[b].Translation, t);
    }

    private static (int A, int B, float T) FindBracket<TKey>(
        TKey[] keys, float time, Func<TKey, float> timeOf)
    {
        if (time <= timeOf(keys[0]))
            return (0, 0, 0f);

        var last = keys.Length - 1;
        if (time >= timeOf(keys[last]))
            return (last, last, 0f);

        for (var i = 1; i <= last; i++)
        {
            var t1 = timeOf(keys[i]);
            if (time <= t1)
            {
                var t0 = timeOf(keys[i - 1]);
                var span = t1 - t0;
                var t = span > 1e-9f ? (time - t0) / span : 0f;
                return (i - 1, i, t);
            }
        }

        return (last, last, 0f);
    }

    private static float WrapTime(float time, float duration)
    {
        if (duration <= 0f || !float.IsFinite(duration))
            return 0f;
        var wrapped = time % duration;
        if (wrapped < 0f)
            wrapped += duration;
        return wrapped;
    }
}
