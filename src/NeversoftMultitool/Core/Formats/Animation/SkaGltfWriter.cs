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
    ///     KNOWN LIMITATION: motion direction is not fully correct. The current
    ///     formula keeps the character upright and produces recognisable walk-
    ///     cycle motion (limbs swing, arms move counter to legs) but legs swing
    ///     somewhat sideways instead of cleanly forward-back, and bones deep in
    ///     long FK chains (e.g. fingers) compound small errors.
    ///
    ///     Empirically tested approaches (THUG1 PS2 Walk1 anim + human.ske + testped):
    ///     - <c>bind * anim</c>  → upright, recognisable but imperfect motion (CURRENT)
    ///     - <c>anim * bind</c>  → same imperfect motion, different sway direction
    ///     - <c>anim</c> alone   → character falls face-down (root rotation lost)
    ///     - <c>inverse(parent_world_bind) * anim</c> (FK approach) → contortion
    ///     - 48 axis-permutation variants tested → best was (-x,+y,-z) on raw
    ///       quat, still imperfect
    ///     - Without conjugating animation quat → sideways/stretched character
    ///
    ///     The THUG source in <c>Sample/thug/Code/Gfx/Skeleton.cpp</c> reveals an
    ///     asymmetry: skeleton bind uses <c>Mth::QuatVecToMatrix</c> (which
    ///     conjugates internally) while skeleton update uses
    ///     <c>sQuatVecToMatrix</c> (which does NOT conjugate). The PS2 inline
    ///     VU0 assembly version exists with a <c>flip</c> mode that negates X
    ///     for handedness conversion. Resolving the exact convention requires
    ///     decompiling the THAW PS2 binary's actual skeleton update — see
    ///     <c>tools/ghidra/thaw-ps2/run_phase_skeleton_anim.sh</c> and
    ///     <c>run_phase_vu0_decomp.sh</c> for the in-progress investigation.
    ///
    ///     Translations use <c>final = bind + anim</c>: non-root bones in walk
    ///     cycles typically have anim translation near zero (bone length is
    ///     constant); treating as a delta preserves skeleton structure.
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
            var bindRotation = skeleton.Bones[i].LocalRotation;
            var bindTranslation = skeleton.Bones[i].LocalTranslation;

            if (track.RotationKeys.Length > 0)
            {
                var rotCurve = node.UseRotation(name);
                foreach (var key in track.RotationKeys)
                    rotCurve.SetPoint(key.Time, bindRotation * key.Rotation);
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
