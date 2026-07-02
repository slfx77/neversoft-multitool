using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
    /// <summary>
    ///     Character-hierarchy populator for PSX models. Emits a skeleton derived
    ///     from the object parent chain (one bone per object), a combined skinned
    ///     mesh that bins faces by material, and per-vertex single-bone influences
    ///     resolved via <see cref="PsxCharacterMeshResolver" /> (stitched + attachment
    ///     vertices follow their source body part).
    ///     PSX character rendering is piecewise-rigid: the engine builds one
    ///     world-space SMatrix per body part and renders each part against its
    ///     own matrix. The exported glTF keeps the authored parent hierarchy for
    ///     bind placement, while animation channels compensate for glTF's parent
    ///     rotation chaining.
    /// </summary>
    private static void PopulatePsxSkinned(
        ModelDocument document,
        PsxMeshFile psxFile,
        PshFile? pshFile,
        MeshChecksumTextureResolver? textureProvider,
        bool flatSkeleton,
        IReadOnlySet<int>? flatBoneIndices)
    {
        var skeletonIndex = document.Skeletons.Count;
        document.Skeletons.Add(BuildPsxSkeleton(
            psxFile, pshFile, flatSkeleton, flatBoneIndices));

        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache = new Dictionary<(uint Hash, bool SemiTransparent), int>();
        var untexturedMaterial = AddMaterial(document, new RenderMaterial
        {
            Name = "untextured",
            BaseColor = new Vector4(0.7f, 0.7f, 0.7f, 1f)
        });

        var lodVariants = BuildPsxLodVariantSet(psxFile);
        var alternateLeafObjects = PsxMeshSemantics.FindAlternateLeafObjectIndices(psxFile);
        var buckets = new Dictionary<int, PsxSkinnedBucket>();

        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            if (alternateLeafObjects.Contains(objectIndex))
                continue;

            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
            if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count || lodVariants.Contains(meshIndex))
                continue;

            var psxMesh = psxFile.Meshes[meshIndex];
            if (psxMesh.Faces.Count == 0)
                continue;

            foreach (var face in psxMesh.Faces)
            {
                var (materialIndex, texDims) = ResolvePsxFaceMaterial(
                    document, face, textureProvider, textureDims, materialCache, untexturedMaterial);

                if (!buckets.TryGetValue(materialIndex, out var bucket))
                {
                    bucket = new PsxSkinnedBucket();
                    buckets[materialIndex] = bucket;
                }

                AddPsxSkinnedFace(
                    bucket.Vertices, bucket.Indices, bucket.Influences,
                    psxFile, objectIndex, meshIndex, psxMesh, face, texDims);
            }
        }

        var combinedMesh = new ModelMesh { Name = "combined_mesh" };
        foreach (var (materialIndex, bucket) in buckets)
        {
            AddPrimitive(combinedMesh, $"mat_{materialIndex:D3}", materialIndex,
                bucket.Vertices, bucket.Indices,
                new ModelSkinBinding
                {
                    SkeletonIndex = skeletonIndex,
                    Influences = bucket.Influences.ToArray()
                });
        }

        AddMeshNode(document, "combined_mesh", combinedMesh);
    }

    private static ModelSkeleton BuildPsxSkeleton(
        PsxMeshFile psxFile,
        PshFile? pshFile,
        bool flatSkeleton,
        IReadOnlySet<int>? flatBoneIndices)
    {
        var skeleton = new ModelSkeleton { Name = "skeleton" };
        var objects = psxFile.Objects;
        var bonePositionsGltf = new Vector3[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            bonePositionsGltf[i] = PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(psxFile, objects[i]));
        }

        for (var i = 0; i < objects.Count; i++)
        {
            var worldBindGltf = bonePositionsGltf[i];
            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, i);
            var name = pshFile?.GetBoneName(i)
                       ?? (meshIndex >= 0 ? ResolvePsxMeshName(psxFile, meshIndex) : null)
                       ?? $"bone_{i}";

            var parent = flatSkeleton || flatBoneIndices?.Contains(i) == true
                ? -1
                : objects[i].ParentIndex;
            if (parent < 0 || parent >= objects.Count || parent == i)
                parent = -1;

            var localBindGltf = parent >= 0
                ? worldBindGltf - bonePositionsGltf[parent]
                : worldBindGltf;

            skeleton.Bones.Add(new ModelBone
            {
                Name = name,
                ParentIndex = parent,
                LocalTransform = Matrix4x4.CreateTranslation(localBindGltf),
                InverseBindMatrix = Matrix4x4.CreateTranslation(-worldBindGltf)
            });
        }

        return skeleton;
    }

    private static (int MaterialIndex, (int Width, int Height) TexDims) ResolvePsxFaceMaterial(
        ModelDocument document,
        PsxFace face,
        MeshChecksumTextureResolver? textureProvider,
        Dictionary<uint, (int Width, int Height)> textureDims,
        Dictionary<(uint Hash, bool SemiTransparent), int> materialCache,
        int untexturedMaterial)
    {
        var key = face.IsTextured && face.TextureHash != 0
            ? (Hash: face.TextureHash, SemiTransparent: face.IsSemiTransparent)
            : (Hash: 0u, SemiTransparent: false);

        var materialIndex = key.Hash == 0
            ? untexturedMaterial
            : GetOrCreatePsxMaterial(document, key.Hash, key.SemiTransparent,
                textureProvider, textureDims, materialCache);

        var texDims = key.Hash != 0 && textureDims.TryGetValue(key.Hash, out var dims)
            ? dims
            : (Width: 256, Height: 256);
        return (materialIndex, texDims);
    }

    private static void AddPsxSkinnedFace(
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences> influences,
        PsxMeshFile psxFile,
        int objectIndex,
        int meshIndex,
        PsxMesh mesh,
        PsxFace face,
        (int Width, int Height) texDims)
    {
        var (c0, c1, c2, c3) = ComputePsxFaceColors(psxFile.Version, face, psxFile.GouraudPalette);
        var v0 = MakePsxSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 0, c0, texDims, out var i0);
        var v1 = MakePsxSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 1, c1, texDims, out var i1);
        var v2 = MakePsxSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 2, c2, texDims, out var i2);
        AddSkinnedTriangle(vertices, indices, influences, v0, i0, v1, i1, v2, i2);

        if (face.IsQuad)
        {
            var v3 = MakePsxSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 3, c3, texDims, out var i3);
            AddSkinnedTriangle(vertices, indices, influences, v1, i1, v3, i3, v2, i2);
        }
    }

    private static ModelVertex MakePsxSkinnedVertex(
        PsxMeshFile psxFile,
        int objectIndex,
        int meshIndex,
        PsxMesh mesh,
        PsxFace face,
        int slot,
        Vector4 color,
        (int Width, int Height) texDims,
        out ModelBoneInfluences influence)
    {
        var vertexIndex = GetPsxFaceVertexIndex(face, slot);
        var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex, vertexIndex);

        var normalMesh = mesh;
        var normalVertexIndex = vertexIndex;
        if (resolved is { UsedAttachment: true, AttachmentResolved: true, SourceMeshIndex: >= 0, SourceVertexIndex: >= 0 }
            && resolved.SourceMeshIndex < psxFile.Meshes.Count)
        {
            var candidate = psxFile.Meshes[resolved.SourceMeshIndex];
            if (candidate.HasPerVertexNormals && resolved.SourceVertexIndex < candidate.VertexCount)
            {
                normalMesh = candidate;
                normalVertexIndex = (uint)resolved.SourceVertexIndex;
            }
        }

        var texCoord = face.GetTextureCoordinate(slot);
        var vertex = new ModelVertex(
            PsxMeshSemantics.ToGltfPosition(resolved.WorldPosition),
            ComputePsxVertexNormal(normalMesh, face, normalVertexIndex),
            color,
            ComputePsxTextureUv(psxFile.Version, face, texCoord.U, texCoord.V, texDims.Width, texDims.Height));

        var jointIndex = resolved.SourceObjectIndex >= 0 ? resolved.SourceObjectIndex : objectIndex;
        influence = ModelBoneInfluences.Single(jointIndex);
        return vertex;
    }

    private sealed class PsxSkinnedBucket
    {
        public List<ModelVertex> Vertices { get; } = [];
        public List<int> Indices { get; } = [];
        public List<ModelBoneInfluences> Influences { get; } = [];
    }

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
    ///     Translation channels are diagnostic while the remaining PSX
    ///     translation-space details are being checked against the engine.
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

        // Animation Tx/Ty/Tz values are copied into SMatrix.t by
        // Decomp_GetAnimTransform and consumed with character vertices shifted
        // right by 4. In exported model units that is the same divisor used for
        // vertex-local positions. The diagnostic channel writer keeps frame 0
        // anchored to bind placement while translation-space parity is checked.
        var translationDivisor = psxFile.ScaleDivisor > 0f
            ? psxFile.ScaleDivisor
            : psxFile.TranslationDivisor;
        if (float.IsFinite(options.TranslationDivisorScale)
            && options.TranslationDivisorScale > 0f)
        {
            translationDivisor *= options.TranslationDivisorScale;
        }
        var fps = options.Fps <= 0f ? PsxAnimationBank.DefaultPreviewFps : options.Fps;

        foreach (var clip in animations)
        {
            var animation = clip.Animation;
            var modelAnim = new ModelAnimation { Name = clip.Name };
            var boneCount = Math.Min(jointCount, animation.BoneCount);
            var frameCount = animation.FrameCount;
            if (boneCount == 0 || frameCount == 0)
                continue;

            var engineParentIndices =
                options.SourceHierarchyTranslation && clip.TranslationParentIndices != null
                    ? NormalizeParentIndices(clip.TranslationParentIndices, boneCount)
                    : BuildPsxEngineParentIndices(psxFile, boneCount);
            if (!options.SkipRotation)
            {
                var rotationContext = new PsxRotationChannelContext(
                    skeletonIndex, animation, gltfParentIndices, boneCount,
                    frameCount, fps, options.RotationCompose, options.LegacyRotationChain,
                    options.RotationScale);
                AppendPsxRotationChannels(modelAnim, rotationContext);
            }

            if (!options.SkipTranslation)
            {
                if (options.EngineWorldTranslation)
                {
                    var translationContext = new PsxTranslationChannelContext(
                        skeletonIndex, skeleton, animation, gltfParentIndices, engineParentIndices, boneCount,
                        frameCount, fps, translationDivisor, options.RotationCompose,
                        options.LegacyRotationChain, options.RotationScale,
                        options.AbsoluteTranslation, options.SkipRotation);
                    AppendPsxEngineWorldTranslationChannels(
                        modelAnim, in translationContext, options.TranslationBoneFilter);
                }
                else
                {
                    for (var bone = 0; bone < boneCount; bone++)
                    {
                        if (options.TranslationBoneFilter is { Count: > 0 } filter
                            && !filter.Contains(bone))
                        {
                            continue;
                        }

                        modelAnim.Channels.Add(BuildPsxTranslationChannel(
                            skeletonIndex, bone, skeleton.Bones[bone], animation,
                            frameCount, fps, translationDivisor, options.AbsoluteTranslation));
                    }
                }
            }

            if (modelAnim.Channels.Count > 0)
                document.Animations.Add(modelAnim);
        }
    }

    /// <summary>
    ///     Bundles the inputs shared across rotation-channel construction so the
    ///     individual builder helpers stay well under the codebase's per-method
    ///     parameter ceiling.
    /// </summary>
    private readonly record struct PsxRotationChannelContext(
        int SkeletonIndex,
        PsxAnimation Animation,
        int[] ParentIndices,
        int BoneCount,
        int FrameCount,
        float Fps,
        PsxRotationCompose Compose,
        bool Legacy,
        float RotationScale);

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

    private static Quaternion[,] MaterialiseEngineLocalRotations(in PsxRotationChannelContext ctx)
    {
        // Materialise per-frame engine-local rotations once so the correction
        // step can read any bone's parent without recomputing trig.
        var engineLocal = new Quaternion[ctx.BoneCount, ctx.FrameCount];
        for (var bone = 0; bone < ctx.BoneCount; bone++)
        {
            var animated = ctx.Animation.IsRotationAnimated(bone);
            for (var frame = 0; frame < ctx.FrameCount; frame++)
            {
                engineLocal[bone, frame] = animated
                    ? ctx.Animation.GetBoneRotation(bone, frame, ctx.Compose, ctx.RotationScale)
                    : Quaternion.Identity;
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
        bool SkipRotation);

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

    private static Quaternion[,] MaterialiseGltfWorldRotations(
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

    private static bool IsUsableParent(int parent, int bone, int boneCount)
    {
        return parent >= 0 && parent < boneCount && parent != bone;
    }

}
