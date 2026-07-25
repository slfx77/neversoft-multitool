using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds glTF animation clips for PSX characters: rotation channels
///     (engine piecewise-rigid absolute rotations re-expressed as glTF
///     parent-chained locals) and the per-clip dispatch that hands
///     translation work to <see cref="PsxTranslationChannelWriter" />.
/// </summary>
internal static class PsxAnimationChannelWriter
{
    /// <summary>
    ///     Adds one <see cref="ModelAnimation" /> to <paramref name="document" />
    ///     per <c>(name, animation)</c> entry.
    ///     Rotation handling matches the engine's piecewise-rigid composition
    ///     (<c>Decomp_GetAnimTransform</c>): each bone's world rotation equals its
    ///     own local Euler rotation, ignoring the parent chain. Real PSX skeletons
    ///     are emitted as flat world-space joints, so the raw local rotations are
    ///     already correct. The parent pre-division path remains for diagnostic
    ///     documents that still use a chained skeleton.
    ///     A bone gets a rotation channel if it has its own non-placeholder
    ///     rotation data OR any ancestor along its parent chain does (otherwise
    ///     glTF would chain the ancestor's animated rotation onto this bone's
    ///     identity bind, mis-rotating it).
    ///     Translation channels follow the engine fixed-point contract: anim
    ///     s16 Tx/Ty/Tz share the model-vertex unit (world×16) and are emitted
    ///     absolute at the vertex ScaleDivisor — the engine rebuilds every bone
    ///     origin from anim data each frame with no bind fallback. Clips whose
    ///     translation streams are entirely zero (placeholder data) keep the
    ///     bind pose instead of collapsing every bone onto its parent.
    /// </summary>
    public static void PopulatePsxAnimations(
        ModelDocument document,
        PsxMeshFile psxFile,
        int skeletonIndex,
        IReadOnlyList<(string Name, PsxAnimation Animation)> animations,
        PsxAnimationOptions options)
    {
        var clips = animations
            .Select(static entry => new PsxAnimationClip(entry.Name, entry.Animation))
            .ToList();
        PopulatePsxAnimationClips(document, psxFile, skeletonIndex, clips, options);
    }

    public static void PopulatePsxAnimationClips(
        ModelDocument document,
        PsxMeshFile psxFile,
        int skeletonIndex,
        IReadOnlyList<PsxAnimationClip> animations,
        PsxAnimationOptions options)
    {
        if ((uint)skeletonIndex >= (uint)document.Skeletons.Count)
            return;
        if (animations.Count == 0)
            return;

        var skeleton = document.Skeletons[skeletonIndex];
        var jointCount = skeleton.Bones.Count;
        var gltfParentIndices = new int[jointCount];
        for (var i = 0; i < jointCount; i++)
            gltfParentIndices[i] = skeleton.Bones[i].ParentIndex;

        // Anim s16 Tx/Ty/Tz are copied raw into SMatrix.t by
        // Decomp_GetAnimTransform and stay at world×16 through the whole
        // hierarchy (the 4.12 rotation cancels the MVMVA >>12). The render path
        // then shifts bone.t and vertices right by 4 together
        // (M3dAsm_SetSuperTransforms / TransformAndOutcodeSuperVertices), so
        // anim translations and model vertices share one unit and one divisor:
        // ScaleDivisor, which already contains that >>4. See the fixed-point
        // contract in tools/diagnostics/psx-anim-format.md.
        var translationDivisor = psxFile.ScaleDivisor > 0f
            ? psxFile.ScaleDivisor
            : psxFile.TranslationDivisor;
        if (float.IsFinite(options.TranslationDivisorScale)
            && options.TranslationDivisorScale > 0f)
        {
            translationDivisor *= options.TranslationDivisorScale;
        }

        var fps = options.Fps <= 0f ? PsxAnimationBank.DefaultPreviewFps : options.Fps;

        // FLAT supers (no HIER chunk — Apocalypse/THPS1-proto era) are a
        // decomp-proven exception to hierarchical composition: the renderer
        // composes EVERY part against the same CSuper base matrix with no
        // bone-to-bone accumulation — part origin = PoseRot·V0_bind + PoseT
        // (two chained MVMVAs at 0x8008445c, Apocalypse final; thps2-psx-proto
        // docs/apocalypse_flat_super_translation.md). In shipped data the
        // pHierarchy bind vectors are ~zero — bruce's pose T values ARE the
        // absolute world-unit part origins (numerically verified: T ≈ obj
        // bind positions). Parent indices are therefore forced flat.
        //
        // NOT the fix for the bruce/hawk arm garble: re-routing pose streams
        // through obj.MeshIndex (pHierarchy.partIndex) was tried and REFUTED
        // by front-view renders (biceps become shoulder slabs) and by the
        // distance-coherence metric (22 vs 17 for positional) — with
        // V0_bind ≈ 0 a consistent mesh+pose pairing is equivalent under
        // either keying, so positional pairing already matches the engine.
        // The arm posing defect has a different, still-unknown cause.
        var flatSuperEngine = !psxFile.HasHierarchy;

        foreach (var clip in animations)
        {
            var animation = clip.Animation;
            var modelAnim = new ModelAnimation { Name = clip.Name };
            var boneCount = Math.Min(jointCount, animation.BoneCount);
            var frameCount = animation.FrameCount;
            if (boneCount == 0 || frameCount == 0)
                continue;

            // Translation hierarchy: the engine composes anim translations
            // through the hierarchy that ships WITH the anim data (pHierarchy /
            // CalculateAnimOrder name-remap), so a clip decoded from an
            // external bank carries that bank's parent table. Fall back to the
            // character's own object hierarchy for embedded anims.
            //
            // v1 direct-matrix clips are exempt from ALL of that: their T
            // cells are absolute model-space part origins even on HIER
            // characters (see PsxAnimation.AbsoluteWorldTranslations —
            // mullen/carnage verified numerically), so composing them through
            // any parent chain double-counts ancestor origins and stretches
            // the body. They always take flat parents + the world-solve path.
            var flatTranslations = flatSuperEngine || clip.Animation.AbsoluteWorldTranslations;
            int[] engineParentIndices;
            if (flatTranslations)
                engineParentIndices = BuildFlatParentIndices(boneCount);
            else if (clip.TranslationParentIndices != null)
                engineParentIndices = NormalizeParentIndices(clip.TranslationParentIndices, boneCount);
            else
                engineParentIndices = BuildPsxEngineParentIndices(psxFile, boneCount);
            if (!options.SkipRotation)
            {
                var rotationContext = new PsxRotationChannelContext(
                    skeletonIndex, animation, gltfParentIndices, boneCount,
                    frameCount, fps, options.RotationCompose, options.LegacyRotationChain,
                    options.RotationScale);
                AppendPsxRotationChannels(modelAnim, rotationContext);
            }

            if (!options.SkipTranslation && PsxTranslationChannelWriter.HasTranslationData(animation, boneCount))
            {
                var translationContext = new PsxTranslationChannelWriter.PsxTranslationChannelContext(
                    skeletonIndex, skeleton, animation, gltfParentIndices, engineParentIndices, boneCount,
                    frameCount, fps, translationDivisor, options.RotationCompose,
                    options.LegacyRotationChain, options.RotationScale,
                    options.AbsoluteTranslation, options.SkipRotation, flatTranslations);
                PsxTranslationChannelWriter.EmitPsxTranslationChannels(modelAnim, in translationContext, options);
            }

            if (modelAnim.Channels.Count > 0)
                document.Animations.Add(modelAnim);
        }
    }

    /// <summary>
    ///     Returns a bone-indexed mask of which bones need a rotation channel.
    ///     In legacy mode this is just <see cref="PsxAnimation.IsRotationAnimated" />.
    ///     In piecewise-rigid mode (default) a bone also needs a channel when any
    ///     ancestor is animated, because glTF would otherwise propagate the
    ///     ancestor's animated rotation through the parent chain.
    /// </summary>
    private static bool[] ComputeRotationChannelMask(in PsxRotationChannelContext ctx)
    {
        var mask = new bool[ctx.BoneCount];
        for (var bone = 0; bone < ctx.BoneCount; bone++)
            mask[bone] = ctx.Animation.IsRotationAnimated(bone);

        if (ctx.Legacy)
            return mask;

        var children = BuildChildLists(ctx.ParentIndices, ctx.BoneCount);
        var pending = new Queue<int>();
        for (var bone = 0; bone < ctx.BoneCount; bone++)
        {
            if (mask[bone])
                pending.Enqueue(bone);
        }

        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var child in children[parent])
            {
                if (mask[child])
                    continue;

                mask[child] = true;
                pending.Enqueue(child);
            }
        }

        return mask;
    }

    private static List<int>[] BuildChildLists(int[] parentIndices, int boneCount)
    {
        var children = new List<int>[boneCount];
        for (var i = 0; i < children.Length; i++)
            children[i] = [];

        for (var child = 0; child < boneCount; child++)
        {
            var parent = parentIndices[child];
            if (parent >= 0 && parent < boneCount && parent != child)
                children[parent].Add(child);
        }

        return children;
    }

    private static int[] BuildPsxEngineParentIndices(PsxMeshFile psxFile, int boneCount)
    {
        var parents = new int[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
        {
            var parent = bone < psxFile.Objects.Count
                ? psxFile.Objects[bone].ParentIndex
                : -1;
            parents[bone] = IsUsableParent(parent, bone, boneCount) ? parent : -1;
        }

        return parents;
    }

    internal static bool ParentIndicesMatch(int[] engineParents, int[] gltfParents, int boneCount)
    {
        for (var bone = 0; bone < boneCount; bone++)
        {
            var gltfParent = bone < gltfParents.Length ? gltfParents[bone] : -1;
            if (!IsUsableParent(gltfParent, bone, boneCount))
                gltfParent = -1;
            if (engineParents[bone] != gltfParent)
                return false;
        }

        return true;
    }

    private static int[] NormalizeParentIndices(IReadOnlyList<int> source, int boneCount)
    {
        var parents = new int[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
        {
            var parent = bone < source.Count ? source[bone] : -1;
            parents[bone] = IsUsableParent(parent, bone, boneCount) ? parent : -1;
        }

        return parents;
    }

    internal static Quaternion[,] MaterialiseEngineLocalRotations(in PsxRotationChannelContext ctx)
    {
        // Materialise per-frame engine-local rotations once so the correction
        // step can read any bone's parent without recomputing trig.
        //
        // v1 direct-matrix clips carry exact matrix-derived quaternions, which
        // are preferred over the Euler round-trip whose YXZ extraction plus
        // System.Numerics recomposition inverted every stored rotation
        // (caught 2026-07-10 by diffing mullen against decomp engine ground
        // truth: matrix quats match to under 2 degrees, the Euler path was
        // ~115 degrees off at posed frames). The Euler path remains for v2
        // streams and for the RotationScale diagnostic, which only exists in
        // angle space.
        var direct = ctx.Animation.DirectRotations;
        var useDirect = direct != null && Math.Abs(ctx.RotationScale - 1f) < 1e-6f;
        var engineLocal = new Quaternion[ctx.BoneCount, ctx.FrameCount];
        for (var bone = 0; bone < ctx.BoneCount; bone++)
        {
            var animated = ctx.Animation.IsRotationAnimated(bone);
            for (var frame = 0; frame < ctx.FrameCount; frame++)
            {
                if (!animated)
                    engineLocal[bone, frame] = Quaternion.Identity;
                else if (useDirect)
                    engineLocal[bone, frame] = direct![bone, frame];
                else
                    engineLocal[bone, frame] =
                        ctx.Animation.GetBoneRotation(bone, frame, ctx.Compose, ctx.RotationScale);
            }
        }

        return engineLocal;
    }

    private static void AppendPsxRotationChannels(ModelAnimation modelAnim, in PsxRotationChannelContext ctx)
    {
        var mask = ComputeRotationChannelMask(in ctx);
        var engineLocal = MaterialiseEngineLocalRotations(in ctx);

        for (var bone = 0; bone < ctx.BoneCount; bone++)
        {
            if (!mask[bone])
                continue;

            modelAnim.Channels.Add(BuildCorrectedRotationChannel(in ctx, engineLocal, bone));
        }
    }

    private static ModelAnimationChannel BuildCorrectedRotationChannel(
        in PsxRotationChannelContext ctx, Quaternion[,] engineLocal, int bone)
    {
        var parent = ctx.ParentIndices[bone];
        var hasUsableParent = !ctx.Legacy && parent >= 0 && parent < ctx.BoneCount;
        var times = new float[ctx.FrameCount];
        var values = new float[ctx.FrameCount * 4];
        var previous = Quaternion.Identity;

        for (var frame = 0; frame < ctx.FrameCount; frame++)
        {
            // Pre-divide by parent.engine_local_rot so glTF's automatic chain
            // composes back to world_rot = engine_local_rot (the engine's
            // piecewise-rigid invariant).
            var psxRot = hasUsableParent
                ? Quaternion.Conjugate(engineLocal[parent, frame]) * engineLocal[bone, frame]
                : engineLocal[bone, frame];
            var gltfRot = new Quaternion(psxRot.X, -psxRot.Y, -psxRot.Z, psxRot.W);

            // Hemisphere normalisation: q and -q encode the same rotation but
            // glTF/Blender SLERP between them takes the long way around (the
            // "spasm" failure mode). Force each key onto the same hemisphere
            // as the previous one. Euler decomposition + parent-chain pre-
            // division can independently flip sign frame-to-frame; flipping is
            // safe because it preserves the underlying rotation.
            if (frame > 0)
            {
                var dot = gltfRot.X * previous.X + gltfRot.Y * previous.Y
                                                 + gltfRot.Z * previous.Z + gltfRot.W * previous.W;
                if (dot < 0f)
                    gltfRot = new Quaternion(-gltfRot.X, -gltfRot.Y, -gltfRot.Z, -gltfRot.W);
            }

            times[frame] = frame / ctx.Fps;
            var offset = frame * 4;
            values[offset] = gltfRot.X;
            values[offset + 1] = gltfRot.Y;
            values[offset + 2] = gltfRot.Z;
            values[offset + 3] = gltfRot.W;
            previous = gltfRot;
        }

        return new ModelAnimationChannel
        {
            SkeletonIndex = ctx.SkeletonIndex,
            BoneIndex = bone,
            Property = ModelAnimationProperty.Rotation,
            Times = times,
            Values = values,
            Interpolation = ModelAnimationInterpolation.Linear
        };
    }

    /// <summary>
    ///     Engine parent table for flat supers: every part is a root. The flat
    ///     renderer path never chains bone to bone (each part composes against
    ///     the shared CSuper base matrix only).
    /// </summary>
    private static int[] BuildFlatParentIndices(int boneCount)
    {
        var parents = new int[boneCount];
        Array.Fill(parents, -1);
        return parents;
    }

    internal static Quaternion[,] MaterialiseGltfWorldRotations(
        in PsxRotationChannelContext ctx,
        Quaternion[,] engineLocalRotations,
        bool skipRotation)
    {
        var world = new Quaternion[ctx.BoneCount, ctx.FrameCount];
        for (var frame = 0; frame < ctx.FrameCount; frame++)
        {
            var computed = new bool[ctx.BoneCount];
            for (var bone = 0; bone < ctx.BoneCount; bone++)
            {
                MaterialiseGltfWorldRotation(
                    in ctx, engineLocalRotations, skipRotation, world, computed, bone, frame);
            }
        }

        return world;
    }

    private static Quaternion MaterialiseGltfWorldRotation(
        in PsxRotationChannelContext ctx,
        Quaternion[,] engineLocalRotations,
        bool skipRotation,
        Quaternion[,] world,
        bool[] computed,
        int bone,
        int frame)
    {
        if (computed[bone])
            return world[bone, frame];

        var local = skipRotation
            ? Quaternion.Identity
            : GetEmittedGltfLocalRotation(in ctx, engineLocalRotations, bone, frame);
        var parent = ctx.ParentIndices[bone];
        world[bone, frame] = IsUsableParent(parent, bone, ctx.BoneCount)
            ? NormalizeQuaternion(MaterialiseGltfWorldRotation(
                in ctx, engineLocalRotations, skipRotation, world, computed, parent, frame) * local)
            : NormalizeQuaternion(local);
        computed[bone] = true;
        return world[bone, frame];
    }

    private static Quaternion GetEmittedGltfLocalRotation(
        in PsxRotationChannelContext ctx,
        Quaternion[,] engineLocalRotations,
        int bone,
        int frame)
    {
        var parent = ctx.ParentIndices[bone];
        var psxRot = !ctx.Legacy && IsUsableParent(parent, bone, ctx.BoneCount)
            ? Quaternion.Conjugate(engineLocalRotations[parent, frame]) * engineLocalRotations[bone, frame]
            : engineLocalRotations[bone, frame];
        return ToGltfRotation(psxRot);
    }

    private static Quaternion ToGltfRotation(Quaternion psxRot)
    {
        return NormalizeQuaternion(new Quaternion(psxRot.X, -psxRot.Y, -psxRot.Z, psxRot.W));
    }

    private static Quaternion NormalizeQuaternion(Quaternion value)
    {
        var lengthSquared = value.LengthSquared();
        return lengthSquared > 0f && float.IsFinite(lengthSquared)
            ? Quaternion.Normalize(value)
            : Quaternion.Identity;
    }

    internal static bool IsUsableParent(int parent, int bone, int boneCount)
    {
        return parent >= 0 && parent < boneCount && parent != bone;
    }

    /// <summary>
    ///     Bundles the inputs shared across rotation-channel construction so the
    ///     individual builder helpers stay well under the codebase's per-method
    ///     parameter ceiling.
    /// </summary>
    internal readonly record struct PsxRotationChannelContext(
        int SkeletonIndex,
        PsxAnimation Animation,
        int[] ParentIndices,
        int BoneCount,
        int FrameCount,
        float Fps,
        PsxRotationCompose Compose,
        bool Legacy,
        float RotationScale);
}
