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
    ///     SKA animation values are treated as absolute local transforms that
    ///     replace the bind pose. However, translation keys of (0,0,0) for
    ///     non-root bones mean "no translation change" — the bind translation
    ///     must be preserved. We detect and skip these by adding the bind
    ///     translation to each animation key.
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
            var bindTranslation = skeleton.Bones[i].LocalTranslation;

            var bindRotation = skeleton.Bones[i].LocalRotation;

            if (track.RotationKeys.Length > 0)
            {
                var rotCurve = node.UseRotation(name);
                foreach (var key in track.RotationKeys)
                {
                    // Compose: final = bind * delta (same model as translation)
                    var final = bindRotation * key.Rotation;
                    rotCurve.SetPoint(key.Time, final);
                }
                channelCount++;
            }

            if (track.TranslationKeys.Length > 0)
            {
                var transCurve = node.UseTranslation(name);
                foreach (var key in track.TranslationKeys)
                    transCurve.SetPoint(key.Time, bindTranslation + key.Translation);
                channelCount++;
            }
        }

        return channelCount;
    }

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
