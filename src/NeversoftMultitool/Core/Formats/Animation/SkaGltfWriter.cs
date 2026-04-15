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

            if (track.RotationKeys.Length > 0 && !IsRotationPlaceholder(track.RotationKeys))
            {
                var rotCurve = node.UseRotation(name);
                foreach (var key in track.RotationKeys)
                    rotCurve.SetPoint(key.Time, key.Rotation);
                channelCount++;
            }

            if (track.TranslationKeys.Length > 0 && !IsTranslationPlaceholder(track.TranslationKeys))
            {
                var transCurve = node.UseTranslation(name);
                foreach (var key in track.TranslationKeys)
                    transCurve.SetPoint(key.Time, key.Translation);
                channelCount++;
            }
        }

        return channelCount;
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
    ///     Write an animated skeleton (no mesh) to a .glb file.
    /// </summary>
    internal static int WriteAnimatedSkeleton(
        Ps2Skeleton skeleton,
        SkaAnimation animation,
        string outputPath,
        string? animationName = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var jointNodes = BuildJointHierarchy(skeleton);
        var name = animationName ?? Path.GetFileNameWithoutExtension(outputPath);
        var channelCount = ApplyAnimation(jointNodes, skeleton, animation, name);
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
}
