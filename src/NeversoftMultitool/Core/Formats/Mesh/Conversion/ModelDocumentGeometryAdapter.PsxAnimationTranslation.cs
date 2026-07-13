using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{

    /// <summary>
    ///     Emits translation channels for one clip. When the translation
    ///     hierarchy differs from the glTF parent chain (external banks), the
    ///     local path would compose through the wrong parents — the world-solve
    ///     path is required; for matching hierarchies both paths are equivalent,
    ///     so the cheaper local emission is kept. Flat supers always take the
    ///     world-solve path: their engine world position carries the
    ///     rotated-bind term that per-node local emission cannot express.
    /// </summary>
    private static void EmitPsxTranslationChannels(
        ModelAnimation modelAnim,
        in PsxTranslationChannelContext ctx,
        PsxAnimationOptions options)
    {
        if (ctx.FlatTranslations
            || options.EngineWorldTranslation
            || !ParentIndicesMatch(ctx.EngineParentIndices, ctx.GltfParentIndices, ctx.BoneCount))
        {
            AppendPsxEngineWorldTranslationChannels(
                modelAnim, in ctx, options.TranslationBoneFilter);
            return;
        }

        for (var bone = 0; bone < ctx.BoneCount; bone++)
        {
            if (options.TranslationBoneFilter is { Count: > 0 } filter
                && !filter.Contains(bone))
            {
                continue;
            }

            modelAnim.Channels.Add(BuildPsxTranslationChannel(
                ctx.SkeletonIndex, bone, ctx.Skeleton.Bones[bone], ctx.Animation,
                ctx.FrameCount, ctx.Fps, ctx.TranslationDivisor, ctx.AbsoluteTranslation));
        }
    }

    /// <summary>
    ///     True when any bone carries a non-zero translation sample. All-zero
    ///     streams are placeholder data; emitting them absolutely would collapse
    ///     every bone onto its parent's origin, so such clips keep bind instead.
    ///     (Per-bone zeros inside a clip that DOES carry translation data are
    ///     engine truth — that bone sits at its parent's origin — and are emitted.)
    /// </summary>
    private static bool HasTranslationData(PsxAnimation animation, int boneCount)
    {
        for (var bone = 0; bone < boneCount; bone++)
        {
            if (animation.IsTranslationAnimated(bone))
                return true;
        }

        return false;
    }

    private static ModelAnimationChannel BuildPsxTranslationChannel(
        int skeletonIndex, int boneIndex, ModelBone bone, PsxAnimation animation,
        int frameCount, float fps, float translationDivisor, bool absoluteTranslation)
    {
        var times = new float[frameCount];
        var values = new float[frameCount * 3];
        var bindTranslation = bone.LocalTransform.Translation;
        var anchorTranslation = animation.GetBoneTranslation(boneIndex, 0) / translationDivisor;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var psxTranslation = animation.GetBoneTranslation(boneIndex, frame) / translationDivisor;
            var psxDelta = psxTranslation - anchorTranslation;
            var gltfT = absoluteTranslation
                ? PsxMeshSemantics.ToGltfPosition(psxTranslation)
                : bindTranslation + PsxMeshSemantics.ToGltfPosition(psxDelta);
            times[frame] = frame / fps;
            var offset = frame * 3;
            values[offset] = gltfT.X;
            values[offset + 1] = gltfT.Y;
            values[offset + 2] = gltfT.Z;
        }

        return new ModelAnimationChannel
        {
            SkeletonIndex = skeletonIndex,
            BoneIndex = boneIndex,
            Property = ModelAnimationProperty.Translation,
            Times = times,
            Values = values,
            Interpolation = ModelAnimationInterpolation.Linear
        };
    }

    private readonly record struct PsxTranslationChannelContext(
        int SkeletonIndex,
        ModelSkeleton Skeleton,
        PsxAnimation Animation,
        int[] GltfParentIndices,
        int[] EngineParentIndices,
        int BoneCount,
        int FrameCount,
        float Fps,
        float TranslationDivisor,
        PsxRotationCompose Compose,
        bool LegacyRotationChain,
        float RotationScale,
        bool AbsoluteTranslation,
        bool SkipRotation,
        bool FlatTranslations = false);


    private static void AppendPsxEngineWorldTranslationChannels(
        ModelAnimation modelAnim,
        in PsxTranslationChannelContext ctx,
        IReadOnlySet<int>? filter)
    {
        var rotationContext = new PsxRotationChannelContext(
            ctx.SkeletonIndex, ctx.Animation, ctx.GltfParentIndices, ctx.BoneCount,
            ctx.FrameCount, ctx.Fps, ctx.Compose, ctx.LegacyRotationChain,
            ctx.RotationScale);
        var engineLocalRotations = MaterialiseEngineLocalRotations(in rotationContext);
        var bindWorldTranslations = MaterialiseBindWorldTranslations(
            ctx.Skeleton, ctx.GltfParentIndices, ctx.BoneCount);
        var engineWorldTranslations = MaterialiseEngineWorldTranslations(
            in ctx, engineLocalRotations);
        var gltfWorldRotations = MaterialiseGltfWorldRotations(
            in rotationContext, engineLocalRotations, ctx.SkipRotation);
        var targetWorldTranslations = MaterialiseTargetWorldTranslations(
            in ctx, bindWorldTranslations, engineWorldTranslations);

        for (var bone = 0; bone < ctx.BoneCount; bone++)
        {
            if (filter is { Count: > 0 } && !filter.Contains(bone))
                continue;

            modelAnim.Channels.Add(BuildSolvedWorldTranslationChannel(
                in ctx, bone, targetWorldTranslations, gltfWorldRotations));
        }
    }

    private static Vector3[] MaterialiseBindWorldTranslations(
        ModelSkeleton skeleton,
        int[] parentIndices,
        int boneCount)
    {
        var world = new Vector3[boneCount];
        var computed = new bool[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
            MaterialiseBindWorldTranslation(skeleton, parentIndices, boneCount, world, computed, bone);

        return world;
    }

    private static Vector3 MaterialiseBindWorldTranslation(
        ModelSkeleton skeleton,
        int[] parentIndices,
        int boneCount,
        Vector3[] world,
        bool[] computed,
        int bone)
    {
        if (computed[bone])
            return world[bone];

        var local = skeleton.Bones[bone].LocalTransform.Translation;
        var parent = parentIndices[bone];
        world[bone] = IsUsableParent(parent, bone, boneCount)
            ? MaterialiseBindWorldTranslation(skeleton, parentIndices, boneCount, world, computed, parent) + local
            : local;
        computed[bone] = true;
        return world[bone];
    }

    private static Vector3[,] MaterialiseEngineWorldTranslations(
        in PsxTranslationChannelContext ctx,
        Quaternion[,] engineLocalRotations)
    {
        var world = new Vector3[ctx.BoneCount, ctx.FrameCount];
        for (var frame = 0; frame < ctx.FrameCount; frame++)
        {
            var computed = new bool[ctx.BoneCount];
            for (var bone = 0; bone < ctx.BoneCount; bone++)
            {
                MaterialiseEngineWorldTranslation(
                    in ctx, engineLocalRotations, world, computed, bone, frame);
            }
        }

        return world;
    }

    private static Vector3 MaterialiseEngineWorldTranslation(
        in PsxTranslationChannelContext ctx,
        Quaternion[,] engineLocalRotations,
        Vector3[,] world,
        bool[] computed,
        int bone,
        int frame)
    {
        if (computed[bone])
            return world[bone, frame];

        // Root bones (and every part of a flat super, whose engine parent
        // table is forced flat): the pose T IS the absolute world-unit part
        // origin — the flat-super renderer (Apocalypse 0x8008445c) adds only
        // a pHierarchy bind vector that ships ~zero in real data.
        var rawTranslation = ctx.Animation.GetBoneTranslation(bone, frame);
        var parent = ctx.EngineParentIndices[bone];
        if (IsUsableParent(parent, bone, ctx.BoneCount))
        {
            var parentWorld = MaterialiseEngineWorldTranslation(
                in ctx, engineLocalRotations, world, computed, parent, frame);
            world[bone, frame] = Vector3.Transform(
                rawTranslation, engineLocalRotations[parent, frame]) + parentWorld;
        }
        else
        {
            world[bone, frame] = rawTranslation;
        }

        computed[bone] = true;
        return world[bone, frame];
    }

    private static Vector3[,] MaterialiseTargetWorldTranslations(
        in PsxTranslationChannelContext ctx,
        Vector3[] bindWorldTranslations,
        Vector3[,] engineWorldTranslations)
    {
        var targets = new Vector3[ctx.BoneCount, ctx.FrameCount];
        for (var bone = 0; bone < ctx.BoneCount; bone++)
        {
            var anchorTranslation = engineWorldTranslations[bone, 0] / ctx.TranslationDivisor;
            for (var frame = 0; frame < ctx.FrameCount; frame++)
            {
                var engineTranslation = engineWorldTranslations[bone, frame] / ctx.TranslationDivisor;
                targets[bone, frame] = ctx.AbsoluteTranslation
                    ? PsxMeshSemantics.ToGltfPosition(engineTranslation)
                    : bindWorldTranslations[bone]
                      + PsxMeshSemantics.ToGltfPosition(engineTranslation - anchorTranslation);
            }
        }

        return targets;
    }

    private static ModelAnimationChannel BuildSolvedWorldTranslationChannel(
        in PsxTranslationChannelContext ctx,
        int bone,
        Vector3[,] targetWorldTranslations,
        Quaternion[,] gltfWorldRotations)
    {
        var times = new float[ctx.FrameCount];
        var values = new float[ctx.FrameCount * 3];
        for (var frame = 0; frame < ctx.FrameCount; frame++)
        {
            var target = targetWorldTranslations[bone, frame];
            var parent = ctx.GltfParentIndices[bone];
            var gltfT = target;
            if (IsUsableParent(parent, bone, ctx.BoneCount))
            {
                var parentDelta = target - targetWorldTranslations[parent, frame];
                gltfT = Vector3.Transform(
                    parentDelta, Quaternion.Conjugate(gltfWorldRotations[parent, frame]));
            }

            times[frame] = frame / ctx.Fps;
            var offset = frame * 3;
            values[offset] = gltfT.X;
            values[offset + 1] = gltfT.Y;
            values[offset + 2] = gltfT.Z;
        }

        return new ModelAnimationChannel
        {
            SkeletonIndex = ctx.SkeletonIndex,
            BoneIndex = bone,
            Property = ModelAnimationProperty.Translation,
            Times = times,
            Values = values,
            Interpolation = ModelAnimationInterpolation.Linear
        };
    }
}
