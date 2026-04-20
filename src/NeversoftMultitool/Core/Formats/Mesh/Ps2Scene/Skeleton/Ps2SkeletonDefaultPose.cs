using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

/// <summary>
///     Populates a THPS4 V1 <see cref="Ps2Skeleton"/>'s bind pose from a companion
///     <c>default.ska.ps2</c> file.
///
///     THPS4 V1 <c>.ske</c> files store only bone names + hierarchy; no neutral pose
///     data. The engine instead loaded a single-frame "default animation" per skeleton
///     archetype (e.g. <c>pre/anims/anims/skater_basics/Default.ska.ps2</c> for the human
///     rig) and applied its frame-0 rotations + translations as the runtime bind pose.
///     The THUG source refers to this as the deprecated "default anims" system in
///     comments at <c>Gfx/Skeleton.cpp:1147-1152</c> — THUG moved the bind into the
///     <c>.ske</c> file itself (V2 format) but THPS4 shipped in the old arrangement.
///
///     Applied by parsing frame 0 of each bone's rotation/translation track from the
///     supplied default anim and recomputing <see cref="Ps2Bone.InverseBindMatrix"/> via
///     the same hierarchy walk used by <see cref="Ps2SkeletonFile"/> for V2 skeletons.
/// </summary>
internal static class Ps2SkeletonDefaultPose
{
    /// <summary>
    ///     Returns a new skeleton with bind poses populated from the default animation's
    ///     first keyframe per bone. Bone hierarchy and identity are preserved; only
    ///     <see cref="Ps2Bone.LocalRotation"/>, <see cref="Ps2Bone.LocalTranslation"/>,
    ///     and <see cref="Ps2Bone.InverseBindMatrix"/> are replaced.
    /// </summary>
    public static Ps2Skeleton EnrichWithDefaultPose(Ps2Skeleton skeleton, SkaAnimation defaultAnim)
    {
        var count = skeleton.Bones.Length;
        var enriched = new Ps2Bone[count];

        // Pass 1: compute local rotation and translation per bone from frame 0.
        for (var i = 0; i < count; i++)
        {
            var src = skeleton.Bones[i];
            var rotation = Quaternion.Identity;
            var translation = Vector3.Zero;

            if (i < defaultAnim.BoneTracks.Length)
            {
                var track = defaultAnim.BoneTracks[i];
                if (track.RotationKeys.Length > 0)
                    rotation = Quaternion.Normalize(track.RotationKeys[0].Rotation);
                if (track.TranslationKeys.Length > 0)
                    translation = track.TranslationKeys[0].Translation;
            }

            enriched[i] = new Ps2Bone
            {
                NameChecksum = src.NameChecksum,
                ParentChecksum = src.ParentChecksum,
                FlipChecksum = src.FlipChecksum,
                ParentIndex = src.ParentIndex,
                LocalRotation = rotation,
                LocalTranslation = translation,
                InverseBindMatrix = Matrix4x4.Identity
            };
        }

        // Pass 2: compute inverse bind matrices by walking the hierarchy.
        // Matches the V2 algorithm in SkeletonFile.TryParseThugFormat / Ps2SkeletonFile.Parse.
        var inverseBindMatrices = new Matrix4x4[count];
        for (var i = 0; i < count; i++)
        {
            var bone = enriched[i];
            var neutralPose = Matrix4x4.CreateFromQuaternion(bone.LocalRotation)
                              * Matrix4x4.CreateTranslation(bone.LocalTranslation);

            if (bone.ParentIndex >= 0)
            {
                Matrix4x4.Invert(inverseBindMatrices[bone.ParentIndex], out var parentForward);
                neutralPose *= parentForward;
                neutralPose.M14 = 0f;
                neutralPose.M24 = 0f;
                neutralPose.M34 = 0f;
                neutralPose.M44 = 1f;
            }

            Matrix4x4.Invert(neutralPose, out var inverseBind);
            inverseBindMatrices[i] = inverseBind;

            enriched[i] = new Ps2Bone
            {
                NameChecksum = bone.NameChecksum,
                ParentChecksum = bone.ParentChecksum,
                FlipChecksum = bone.FlipChecksum,
                ParentIndex = bone.ParentIndex,
                LocalRotation = bone.LocalRotation,
                LocalTranslation = bone.LocalTranslation,
                InverseBindMatrix = inverseBind
            };
        }

        return new Ps2Skeleton
        {
            Version = skeleton.Version,
            Flags = skeleton.Flags,
            Bones = enriched
        };
    }
}
