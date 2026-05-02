using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Writes an animated skeleton (and optionally a skinned mesh) to a .glb file
///     using SharpGLTF Toolkit. Animation keyframes from <see cref="SkaAnimation"/>
///     are applied as rotation/translation channels on the skeleton's joint nodes.
/// </summary>
internal static class SkaGltfWriter
{
    /// <summary>
    ///     Build joint hierarchy from a parsed skeleton, matching the bone builder
    ///     in Ps2SceneGltfWriter.BuildSkinned().
    /// </summary>
    internal static NodeBuilder[] BuildJointHierarchy(Ps2Skeleton skeleton)
    {
        var jointNodes = new NodeBuilder[skeleton.Bones.Length];
        for (var i = 0; i < skeleton.Bones.Length; i++)
        {
            var bone = skeleton.Bones[i];
            var boneName = $"bone_{bone.NameChecksum:X8}";

            if (bone.ParentIndex < 0)
                jointNodes[i] = new NodeBuilder(boneName);
            else
                jointNodes[i] = jointNodes[bone.ParentIndex].CreateNode(boneName);

            jointNodes[i].LocalTransform = new AffineTransform(
                null,
                bone.LocalRotation,
                bone.LocalTranslation);
        }

        return jointNodes;
    }

    /// <summary>
    ///     Build joint hierarchy seeded by the animation's first keyframe rather than
    ///     the skeleton's static bind pose. Used by THPS4 (version 1) content where
    ///     the native .ske file has no neutral pose — the animation's frame 0 IS the
    ///     rest pose.
    ///
    ///     For each bone:
    ///       - Translation: first animation translation key if present, else bone bind.
    ///       - Rotation: first animation rotation key if present, else bone bind.
    ///
    ///     Version-1 suppression: when a bone's <c>NameChecksum</c> does NOT resolve
    ///     via <see cref="QbKey.TryResolve"/>, any constant rest rotation from the
    ///     animation is suppressed back to identity. This matches the historical
    ///     behavior where unresolved bones (often auxiliary/attachment points) would
    ///     otherwise lock into an arbitrary constant and produce broken poses.
    /// </summary>
    internal static NodeBuilder[] BuildJointHierarchy(Ps2Skeleton skeleton, SkaAnimation animation)
    {
        var jointNodes = new NodeBuilder[skeleton.Bones.Length];
        var tracksByBoneIndex = BuildBoneTrackIndex(animation, skeleton.Bones.Length);

        for (var i = 0; i < skeleton.Bones.Length; i++)
        {
            var bone = skeleton.Bones[i];
            var boneName = $"bone_{bone.NameChecksum:X8}";

            if (bone.ParentIndex < 0)
                jointNodes[i] = new NodeBuilder(boneName);
            else
                jointNodes[i] = jointNodes[bone.ParentIndex].CreateNode(boneName);

            var track = tracksByBoneIndex[i];
            var translation = bone.LocalTranslation;
            if (track != null && track.TranslationKeys.Length > 0)
                translation = track.TranslationKeys[0].Translation;

            var rotation = bone.LocalRotation;
            if (track != null && track.RotationKeys.Length > 0)
                rotation = track.RotationKeys[0].Rotation;

            // Version-1 suppression: an unresolved bone carrying a CONSTANT (single-key)
            // rest rotation is replaced with identity so arbitrary per-bone orientations
            // don't leak into the exported rest pose. Multi-key tracks always represent
            // real animation and are preserved; resolved bones ("root" etc.) also keep
            // their constant rotation on the assumption that the author intended it.
            if (skeleton.Version == 1
                && track != null
                && track.RotationKeys.Length == 1
                && global::NeversoftMultitool.Core.QbKey.QbKey.TryResolve(bone.NameChecksum) == null)
            {
                rotation = Quaternion.Identity;
            }

            jointNodes[i].LocalTransform = new AffineTransform(null, rotation, translation);
        }

        return jointNodes;
    }

    /// <summary>
    ///     Build a joint hierarchy whose rest pose is seeded directly from
    ///     <see cref="SkaPoseEvaluator.Evaluate"/> at <c>t = 0</c>. Use this variant
    ///     when downstream consumers compare joint LocalTransforms against evaluator
    ///     samples — the two are guaranteed to agree.
    /// </summary>
    internal static NodeBuilder[] BuildJointHierarchySeededFromEvaluator(
        Ps2Skeleton skeleton, SkaAnimation animation)
    {
        var jointNodes = new NodeBuilder[skeleton.Bones.Length];
        var initialPoses = new SkaPoseEvaluator(animation, skeleton).Evaluate(0f);

        for (var i = 0; i < skeleton.Bones.Length; i++)
        {
            var bone = skeleton.Bones[i];
            var boneName = $"bone_{bone.NameChecksum:X8}";

            if (bone.ParentIndex < 0)
                jointNodes[i] = new NodeBuilder(boneName);
            else
                jointNodes[i] = jointNodes[bone.ParentIndex].CreateNode(boneName);

            jointNodes[i].LocalTransform = new AffineTransform(
                null, initialPoses[i].Rotation, initialPoses[i].Translation);
        }

        return jointNodes;
    }

    private static SkaBoneTrack?[] BuildBoneTrackIndex(SkaAnimation animation, int boneCount)
    {
        var index = new SkaBoneTrack?[boneCount];
        foreach (var track in animation.BoneTracks)
        {
            if (track.BoneIndex >= 0 && track.BoneIndex < boneCount)
                index[track.BoneIndex] = track;
        }

        return index;
    }

    /// <summary>
    ///     Apply animation channels to an existing joint hierarchy.
    ///     Returns the number of channels added.
    ///
    ///     SKA stores absolute local rotations and translations per bone; the
    ///     THUG runtime (<c>CSkeleton::Update</c> in <c>Sample/thug/Code/Gfx/Skeleton.cpp</c>)
    ///     hands these directly to <c>sQuatVecToMatrix</c> as the bone's local
    ///     transform. We mirror that — no composition with bind pose.
    ///
    ///     SKA encodes "no animation for this bone" as a single placeholder key:
    ///     identity rotation <c>(0,0,0,1)</c> or zero translation. The runtime
    ///     leaves the pose entry at its bind value when this happens; emitting
    ///     a glTF channel with the placeholder would instead force the bone to
    ///     identity and collapse the skin. <see cref="IsRotationPlaceholder"/>
    ///     and <see cref="IsTranslationPlaceholder"/> suppress those channels.
    ///
    ///     Requires the joint nodes' <c>LocalTransform</c> to be the bind pose
    ///     (so bones with suppressed channels stay at bind), and the skin to be
    ///     attached via <c>AddSkinnedMesh(mesh, (joint, IBM)[])</c> with explicit
    ///     IBMs from <c>Ps2Skeleton.InverseBindMatrix</c>.
    /// </summary>
    internal static int ApplyAnimation(
        NodeBuilder[] jointNodes,
        Ps2Skeleton skeleton,
        SkaAnimation animation,
        string? animationName = null)
    {
        var boneCount = Math.Min(animation.BoneTracks.Length, jointNodes.Length);
        var name = animationName ?? "animation";
        var channelCount = 0;

        for (var i = 0; i < boneCount; i++)
        {
            var track = animation.BoneTracks[i];
            var node = jointNodes[i];

            if (track.RotationKeys.Length > 0 && !ShouldSuppressRotation(track.RotationKeys, skeleton.Version))
            {
                var rotCurve = node.UseRotation(name);
                foreach (var key in track.RotationKeys)
                    rotCurve.SetPoint(key.Time, key.Rotation);
                channelCount++;
            }

            if (track.TranslationKeys.Length > 0 && !ShouldSuppressTranslation(track.TranslationKeys, skeleton.Version))
            {
                var transCurve = node.UseTranslation(name);
                foreach (var key in track.TranslationKeys)
                    transCurve.SetPoint(key.Time, key.Translation);
                channelCount++;
            }
        }

        return channelCount;
    }

    /// <summary>
    ///     Apply multiple named animations as separate glTF animation tracks.
    ///     Each animation becomes a switchable track in the output (compatible with
    ///     model-viewer / Blender / Unity). Returns total channel count across all
    ///     animations.
    /// </summary>
    internal static int ApplyAnimations(
        NodeBuilder[] jointNodes,
        Ps2Skeleton skeleton,
        IReadOnlyList<(string Name, SkaAnimation Animation)> animations)
    {
        var totalChannels = 0;
        foreach (var (name, animation) in animations)
            totalChannels += ApplyAnimation(jointNodes, skeleton, animation, name);
        return totalChannels;
    }

    // Version-aware suppression: version-1 (THPS4) bakes constant tracks into the
    // joint's rest pose via BuildJointHierarchy(skeleton, animation), so any
    // single-key track is redundant and we skip emitting it as a channel. For
    // version-2+ content, only the historical identity/zero placeholders are
    // suppressed (those encode "no animation for this bone").
    private static bool ShouldSuppressRotation(SkaRotationKey[] keys, int skeletonVersion)
    {
        // Version-1 skeletons bake constant tracks into the joint rest pose via
        // BuildJointHierarchy(skeleton, animation), so single-key tracks become
        // redundant and we omit the channel.
        if (skeletonVersion == 1 && keys.Length == 1)
            return true;
        return IsRotationPlaceholder(keys);
    }

    private static bool ShouldSuppressTranslation(SkaTranslationKey[] keys, int skeletonVersion)
    {
        if (skeletonVersion == 1 && keys.Length == 1)
            return true;
        return IsTranslationPlaceholder(keys);
    }

    // SKA encodes "no animation" for a bone as a single identity rotation key
    // or a single zero translation key. The runtime initializes the pose to bind
    // and only overwrites entries with real motion. For glTF, suppress these
    // placeholder channels so the joint node keeps its bind-pose rest transform.
    private static bool IsRotationPlaceholder(SkaRotationKey[] keys)
        => keys.Length == 1 && keys[0].Rotation == Quaternion.Identity;

    private static bool IsTranslationPlaceholder(SkaTranslationKey[] keys)
        => keys.Length == 1 && keys[0].Translation == Vector3.Zero;

    /// <summary>
    ///     Write an animated skeleton (no mesh) to a .glb file with one or more
    ///     named animation tracks.
    /// </summary>
    internal static int WriteAnimatedSkeleton(
        Ps2Skeleton skeleton,
        IReadOnlyList<(string Name, SkaAnimation Animation)> animations,
        string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var jointNodes = BuildJointHierarchy(skeleton);
        var channelCount = ApplyAnimations(jointNodes, skeleton, animations);
        if (channelCount == 0)
            return 0;

        // Build scene with animated skeleton (no mesh, just bones)
        var scene = new SceneBuilder();
        // Add root nodes to scene so they appear in the output
        foreach (var joint in jointNodes)
        {
            if (joint.Parent == null)
                scene.AddNode(joint);
        }

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);
        return channelCount;
    }

    /// <summary>
    ///     Backward-compatible single-animation overload.
    /// </summary>
    internal static int WriteAnimatedSkeleton(
        Ps2Skeleton skeleton,
        SkaAnimation animation,
        string outputPath,
        string? animationName = null)
    {
        var name = animationName ?? Path.GetFileNameWithoutExtension(outputPath);
        return WriteAnimatedSkeleton(skeleton, [(name, animation)], outputPath);
    }
}
