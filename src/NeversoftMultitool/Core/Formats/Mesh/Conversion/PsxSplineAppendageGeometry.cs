using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Reconstructs Spider-Man's runtime spline appendages from the seven-box
///     controller chains stored in the character PSX. The boxes are editor and
///     animation controls, not render geometry. The game skins a tube through
///     their transforms and, for Ock, instances a structurally discovered
///     sibling tip kit at each endpoint. Keeping the generated rings
///     single-bone weighted to their corresponding controls lets the ordinary
///     exported animation channels drive the reconstructed surface.
/// </summary>
internal static class PsxSplineAppendageGeometry
{
    private const int ControllersPerChain = 7;
    private const int TubeSides = 8;

    /// <summary>
    ///     Discovers appendage controls using the selected model and its own
    ///     embedded animation bank. One-chain rigs need animation evidence to
    ///     distinguish Scorpion's live tail from Lizard's abandoned editor
    ///     controls; four-chain Ock rigs are structurally unambiguous.
    /// </summary>
    internal static IReadOnlyList<PsxSplineControllerChain> DiscoverControllerChains(
        PsxMeshFile psxFile,
        AssetSource source,
        byte[] sourceBytes)
    {
        var structural = FindStructuralControllerChains(psxFile);
        if (structural.Count != 1)
            return structural;

        var animations = DecodeEmbeddedAnimationEvidence(
            source, sourceBytes, psxFile.Objects.Count);
        return ValidateSingleControllerChain(psxFile, structural[0], animations);
    }

    internal static IReadOnlyList<PsxSplineControllerChain> FindControllerChains(
        PsxMeshFile psxFile,
        IReadOnlyList<PsxAnimation>? animationEvidence = null)
    {
        var structural = FindStructuralControllerChains(psxFile);
        return structural.Count == 1
            ? ValidateSingleControllerChain(psxFile, structural[0], animationEvidence ?? [])
            : structural;
    }

    private static List<PsxSplineControllerChain> FindStructuralControllerChains(
        PsxMeshFile psxFile)
    {
        if (!PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(psxFile)
            || psxFile.Objects.Count < ControllersPerChain)
        {
            return [];
        }

        // Every known spline super stores its controller boxes as one terminal
        // run: seven for Scorpion's tail, or four groups of seven for Ock's
        // tentacles. Requiring that complete structural signature avoids
        // mistaking ordinary cube props (including control.psx) for splines.
        var firstController = psxFile.Objects.Count;
        while (firstController > 0 && IsControllerCube(psxFile, firstController - 1))
            firstController--;

        var controllerCount = psxFile.Objects.Count - firstController;
        if (controllerCount is not ControllersPerChain and not 4 * ControllersPerChain)
            return [];

        var chains = new List<PsxSplineControllerChain>(controllerCount / ControllersPerChain);
        for (var start = firstController; start < psxFile.Objects.Count; start += ControllersPerChain)
        {
            var objectIndices = Enumerable.Range(start, ControllersPerChain).ToArray();
            var centers = objectIndices
                .Select(index => PsxMeshSemantics.GetObjectOffset(psxFile, psxFile.Objects[index]))
                .ToArray();
            var distances = centers.Zip(centers.Skip(1), Vector3.Distance).ToArray();
            var average = distances.Average();
            if (average is < 20f or > 80f
                || distances.Any(distance => MathF.Abs(distance - average) > average * 0.08f))
            {
                return [];
            }

            // The runtime treats every box in a spline as a peer under one
            // character-space parent. Root controls use -1 on PC and are
            // valid; only disagreement within the run is a rejection.
            if (objectIndices
                .Select(index => psxFile.Objects[index].ParentIndex)
                .Distinct()
                .Take(2)
                .Count() != 1)
            {
                return [];
            }

            chains.Add(new PsxSplineControllerChain(objectIndices, centers));
        }

        return chains;
    }

    private static IReadOnlyList<PsxSplineControllerChain> ValidateSingleControllerChain(
        PsxMeshFile psxFile,
        PsxSplineControllerChain chain,
        IReadOnlyList<PsxAnimation> animations)
    {
        if (animations.Count == 0)
            return [];

        var endpointObjectIndex = chain.ObjectIndices[^1];
        var parentIndex = psxFile.Objects[endpointObjectIndex].ParentIndex;
        var controllerObjects = chain.ObjectIndices.ToHashSet();
        var candidates = Enumerable.Range(0, psxFile.Objects.Count)
            .Where(index => !controllerObjects.Contains(index))
            .Where(index => psxFile.Objects[index].ParentIndex == parentIndex)
            .Where(index => IsDrawableObject(psxFile, index))
            .Where(index => HasEndpointAnimationSignature(
                index, endpointObjectIndex, animations))
            .Take(2)
            .ToArray();
        if (candidates.Length != 1)
            return [];

        return [chain with { EmbeddedTipObjectIndex = candidates[0] }];
    }

    private static bool IsDrawableObject(PsxMeshFile psxFile, int objectIndex)
    {
        var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
        return meshIndex >= 0
               && meshIndex < psxFile.Meshes.Count
               && psxFile.Meshes[meshIndex].Faces.Count > 0;
    }

    private static bool HasEndpointAnimationSignature(
        int tipObjectIndex,
        int endpointObjectIndex,
        IReadOnlyList<PsxAnimation> animations)
    {
        var sawAnimation = false;
        var sawSharedTranslationTrack = false;
        var sawRotationAsymmetry = false;
        foreach (var animation in animations)
        {
            if (tipObjectIndex >= animation.BoneCount
                || endpointObjectIndex >= animation.BoneCount
                || animation.FrameCount <= 0)
            {
                continue;
            }

            sawAnimation = true;
            for (var frame = 0; frame < animation.FrameCount; frame++)
            {
                for (var channel = 3; channel < PsxAnimation.ChannelsPerBone; channel++)
                {
                    if (animation.Channels[tipObjectIndex, channel, frame]
                        != animation.Channels[endpointObjectIndex, channel, frame])
                    {
                        return false;
                    }
                }
            }

            // An all-zero translation grid is the codec's placeholder, not an
            // endpoint relationship. Equality above proves that a non-zero
            // endpoint track is shared by the candidate tip as well.
            if (animation.IsTranslationAnimated(endpointObjectIndex))
                sawSharedTranslationTrack = true;

            if (animation.FrameCount > 1
                && animation.IsRotationAnimated(tipObjectIndex)
                && !animation.IsRotationAnimated(endpointObjectIndex))
            {
                sawRotationAsymmetry = true;
            }
        }

        return sawAnimation && sawSharedTranslationTrack && sawRotationAsymmetry;
    }

    private static PsxAnimation[] DecodeEmbeddedAnimationEvidence(
        AssetSource source,
        byte[] sourceBytes,
        int boneCount)
    {
        var bank = PsxAnimationBank.TryProbe(source, sourceBytes, boneCount);
        if (bank == null || bank.BoneCount != boneCount)
            return [];

        var selections = PsxAnimationBank.ResolveSelections(
            bank.AnimFile, -1, null, null);
        return PsxAnimationBank.Decode(bank, boneCount, selections)
            .Animations
            .Select(static entry => entry.Animation)
            .ToArray();
    }

    internal static HashSet<int> BuildControllerObjectSet(
        IReadOnlyList<PsxSplineControllerChain> chains)
    {
        return chains.SelectMany(static chain => chain.ObjectIndices).ToHashSet();
    }

    /// <summary>
    ///     Finds the authored spline skin bundled beside the claw geometry.
    ///     Spider-Man's claw asset carries one ordinary texture used by the
    ///     rendered claw and additional square strip textures that are not
    ///     referenced by its ordinary face list. The level object banks carry
    ///     the same content: those otherwise-unused images are the runtime
    ///     tentacle skins, not unrelated character textures.
    /// </summary>
    internal static uint? FindTubeTextureHash(
        PsxMeshFile clawFile,
        MeshChecksumTextureResolver? textureProvider)
    {
        if (textureProvider == null)
            return null;

        for (var meshIndex = clawFile.Meshes.Count - 1; meshIndex >= 0; meshIndex--)
        {
            if (FindMappedTubeTextureHash(clawFile, meshIndex, textureProvider) is { } mapped)
                return mapped;
        }

        return FindUnusedSquareTextureHash(clawFile, textureProvider);
    }

    internal static uint? FindTubeTextureHash(
        PsxMeshFile clawFile,
        int meshIndex,
        MeshChecksumTextureResolver? textureProvider)
    {
        if (textureProvider == null)
            return null;

        return FindMappedTubeTextureHash(clawFile, meshIndex, textureProvider)
               ?? FindUnusedSquareTextureHash(clawFile, textureProvider);
    }

    internal static uint? FindMappedTubeTextureHash(
        PsxMeshFile clawFile,
        int meshIndex,
        MeshChecksumTextureResolver textureProvider)
    {
        if (meshIndex < 0 || meshIndex >= clawFile.Meshes.Count)
            return null;

        var renderedTextureHashes = clawFile.Meshes
            .SelectMany(static mesh => mesh.Faces)
            .Where(static face => face.IsTextured && face.TextureHash != 0)
            .Select(static face => face.TextureHash)
            .ToHashSet();

        // The claw's terminal invisible face is a fully UV-mapped spline
        // template. Its preceding invisible face carries a degenerate preload
        // mapping, while unrelated unused texture slots have no template face
        // at all. Follow that source association instead of relying on texture
        // table order.
        foreach (var faceRead in clawFile.Meshes[meshIndex].FaceReadInfos.Reverse())
        {
            var textureHash = faceRead.TextureHash;
            if (textureHash == 0 || renderedTextureHashes.Contains(textureHash))
                continue;
            if (faceRead.IsAccepted
                || !string.Equals(
                    faceRead.RejectionReason,
                    "invisible (M3dInit STP toggle)",
                    StringComparison.Ordinal)
                || !HasUsableTemplateMapping(faceRead))
            {
                continue;
            }

            var pngBytes = textureProvider(textureHash);
            if (!IsSquareSplineTexture(pngBytes))
                continue;

            return textureHash;
        }

        return null;
    }

    private static uint? FindUnusedSquareTextureHash(
        PsxMeshFile clawFile,
        MeshChecksumTextureResolver textureProvider)
    {
        var renderedTextureHashes = clawFile.Meshes
            .SelectMany(static mesh => mesh.Faces)
            .Where(static face => face.IsTextured && face.TextureHash != 0)
            .Select(static face => face.TextureHash)
            .ToHashSet();
        // Older synthetic callers may not retain raw face diagnostics. A lone
        // unused square texture is still unambiguous; multiple candidates are
        // deliberately rejected rather than guessing by slot order.
        var fallbackCandidates = clawFile.TextureHashes
            .Where(textureHash => textureHash != 0 && !renderedTextureHashes.Contains(textureHash))
            .Distinct()
            .Where(textureHash => IsSquareSplineTexture(textureProvider(textureHash)))
            .Take(2)
            .ToArray();
        return fallbackCandidates.Length == 1 ? fallbackCandidates[0] : null;
    }

    /// <summary>
    ///     Finds the skin for a one-chain appendage from its authored endpoint
    ///     mesh. Scorpion's hook is stored in the character itself and all of
    ///     its drawable faces use the same 64x64 blue/green strip. The runtime
    ///     continues that image along the procedural tail. Once the endpoint
    ///     mesh has been identified, requiring one placed endpoint and one
    ///     resolvable square texture avoids adding a character, filename, or
    ///     texture-hash special case here.
    /// </summary>
    internal static uint? FindEmbeddedTailTextureHash(
        PsxMeshFile psxFile,
        IReadOnlyDictionary<int, PsxSplineTipPlacement> tipPlacements,
        MeshChecksumTextureResolver? textureProvider)
    {
        if (textureProvider == null || tipPlacements.Count != 1)
            return null;

        var objectIndex = tipPlacements.Keys.Single();
        var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
        if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count)
            return null;

        var candidates = psxFile.Meshes[meshIndex].Faces
            .Where(static face => face.IsTextured && face.TextureHash != 0)
            .Select(static face => face.TextureHash)
            .Distinct()
            .Where(textureHash => IsSquareSplineTexture(textureProvider(textureHash)))
            .Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool HasUsableTemplateMapping(PsxFaceReadInfo faceRead)
    {
        var vertexCount = (faceRead.Flags & 0x0010) == 0 ? 4 : 3;
        if (faceRead.TextureCoordinates.Count < vertexCount)
            return false;

        // PSX quads are emitted as (0,2,1) and (1,2,3). Treating their four
        // slots as a polygon can report zero for the valid strip ordering
        // 0,1 / 0,0 / 1,1 / 1,0 because that order self-crosses. Test the
        // actual rendered triangles instead.
        return TriangleUvArea(faceRead.TextureCoordinates, 0, 2, 1) != 0
               || (vertexCount == 4
                   && TriangleUvArea(faceRead.TextureCoordinates, 1, 2, 3) != 0);
    }

    private static long TriangleUvArea(
        IReadOnlyList<PsxTextureCoordinate> textureCoordinates,
        int first,
        int second,
        int third)
    {
        var a = textureCoordinates[first];
        var b = textureCoordinates[second];
        var c = textureCoordinates[third];
        return (long)(b.U - a.U) * (c.V - a.V)
               - (long)(c.U - a.U) * (b.V - a.V);
    }

    private static bool IsSquareSplineTexture(byte[]? pngBytes)
    {
        return pngBytes != null
               && ModelDocumentGeometryAdapter.TryExtractPngDimensions(pngBytes) is { } dimensions
               && dimensions.Width == dimensions.Height
               && dimensions.Width >= 32;
    }

    internal static IReadOnlyDictionary<int, PsxSplineTipPlacement> FindEmbeddedTipPlacements(
        PsxMeshFile psxFile,
        IReadOnlyList<PsxSplineControllerChain> chains)
    {
        if (chains.Count != 1 || chains[0].EmbeddedTipObjectIndex is not { } objectIndex)
            return new Dictionary<int, PsxSplineTipPlacement>();

        return new Dictionary<int, PsxSplineTipPlacement>
        {
            [objectIndex] = CreateTipPlacement(chains[0])
        };
    }

    internal static void AppendGeneratedTubes(
        IReadOnlyList<PsxSplineControllerChain> chains,
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences> influences,
        bool hasAuthoredTexture)
    {
        var isTail = chains.Count == 1;
        foreach (var chain in chains)
        {
            AppendTube(chain, isTail, vertices, indices, influences, hasAuthoredTexture);
        }
    }

    private static bool IsControllerCube(PsxMeshFile psxFile, int objectIndex)
    {
        var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
        if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count)
            return false;

        var mesh = psxFile.Meshes[meshIndex];
        if (mesh.Vertices.Count != 8
            || mesh.Faces.Count != 6
            || mesh.Vertices.Any(static vertex => vertex.Type != 0)
            || mesh.Faces.Any(static face => !face.IsQuad || face.IsTextured))
        {
            return false;
        }

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in mesh.Vertices)
        {
            var position = new Vector3(vertex.X, vertex.Y, vertex.Z);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var size = max - min;
        var smallest = MathF.Min(size.X, MathF.Min(size.Y, size.Z));
        var largest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        return smallest >= 5f && largest <= 30f && smallest / largest >= 0.9f;
    }

    private static void AppendTube(
        PsxSplineControllerChain chain,
        bool taperTail,
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences> influences,
        bool hasAuthoredTexture)
    {
        var averageSegmentLength = chain.Centers
            .Zip(chain.Centers.Skip(1), Vector3.Distance)
            .Average();
        var baseRadius = Math.Clamp(averageSegmentLength / 10f, 3.25f, 4.75f);
        var samples = BuildSmoothedSamples(chain);
        var sampleCenters = samples.Select(static sample => sample.Center).ToArray();
        var frames = BuildTransportFrames(sampleCenters);
        var longitudinalDistances = new float[samples.Count];
        for (var sampleIndex = 1; sampleIndex < samples.Count; sampleIndex++)
        {
            longitudinalDistances[sampleIndex] = longitudinalDistances[sampleIndex - 1]
                                                 + Vector3.Distance(
                                                     sampleCenters[sampleIndex - 1],
                                                     sampleCenters[sampleIndex]);
        }

        // Match longitudinal and circumferential texel density. UVs may exceed
        // one along a chain so the authored banded strip repeats instead of
        // stretching once across an entire tentacle.
        var textureRepeatLength = 2f * MathF.PI * baseRadius;
        var rings = new ModelVertex[samples.Count][];

        for (var ringIndex = 0; ringIndex < samples.Count; ringIndex++)
        {
            var (normal, binormal) = frames[ringIndex];
            var radius = taperTail
                ? baseRadius * (1f - 0.45f * ringIndex / (samples.Count - 1f))
                : baseRadius;
            // Duplicate side zero at side eight. Sharing it would interpolate
            // UV .875 back to zero across the closing face and create a broad
            // texture seam even though the positions themselves are joined.
            rings[ringIndex] = new ModelVertex[TubeSides + 1];
            for (var side = 0; side <= TubeSides; side++)
            {
                var angle = 2f * MathF.PI * side / TubeSides;
                var radial = normal * MathF.Cos(angle) + binormal * MathF.Sin(angle);
                var texCoord = hasAuthoredTexture
                    ? new Vector2(
                        longitudinalDistances[ringIndex] / textureRepeatLength,
                        side / (float)TubeSides)
                    : new Vector2(side / (float)TubeSides,
                        ringIndex / (float)(samples.Count - 1));
                rings[ringIndex][side] = new ModelVertex(
                    PsxMeshSemantics.ToGltfPosition(samples[ringIndex].Center + radial * radius),
                    Vector3.Normalize(PsxMeshSemantics.ToGltfPosition(radial)),
                    hasAuthoredTexture ? Vector4.One : new Vector4(0.58f, 0.60f, 0.63f, 1f),
                    texCoord);
            }
        }

        for (var ringIndex = 0; ringIndex < rings.Length - 1; ringIndex++)
        {
            var firstInfluence = samples[ringIndex].Influence;
            var secondInfluence = samples[ringIndex + 1].Influence;
            for (var side = 0; side < TubeSides; side++)
            {
                var next = side + 1;
                ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                    vertices, indices, influences,
                    rings[ringIndex][side], firstInfluence,
                    rings[ringIndex][next], firstInfluence,
                    rings[ringIndex + 1][side], secondInfluence);
                ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                    vertices, indices, influences,
                    rings[ringIndex][next], firstInfluence,
                    rings[ringIndex + 1][next], secondInfluence,
                    rings[ringIndex + 1][side], secondInfluence);
            }
        }
    }

    private static List<PsxSplineSample> BuildSmoothedSamples(PsxSplineControllerChain chain)
    {
        var samples = new List<PsxSplineSample>(2 * (chain.Centers.Count - 1) + 2)
        {
            new(chain.Centers[0], ModelBoneInfluences.Single(chain.ObjectIndices[0]))
        };

        // Chaikin corner cutting is deliberately used instead of inventing
        // extra animation controls: two positive-weight samples per authored
        // span bevel the seven-point polyline, and ordinary two-joint glTF
        // skinning keeps those samples between the same controller transforms.
        for (var index = 0; index < chain.Centers.Count - 1; index++)
        {
            foreach (var amount in new[] { 0.25f, 0.75f })
            {
                samples.Add(new PsxSplineSample(
                    Vector3.Lerp(chain.Centers[index], chain.Centers[index + 1], amount),
                    new ModelBoneInfluences(
                        chain.ObjectIndices[index],
                        chain.ObjectIndices[index + 1],
                        0,
                        0,
                        1f - amount,
                        amount,
                        0f,
                        0f)));
            }
        }

        var last = chain.Centers.Count - 1;
        samples.Add(new PsxSplineSample(
            chain.Centers[last], ModelBoneInfluences.Single(chain.ObjectIndices[last])));
        return samples;
    }

    internal static int DetermineTipForwardSign(PsxMesh tipMesh)
    {
        ArgumentNullException.ThrowIfNull(tipMesh);
        if (tipMesh.Vertices.Count == 0)
            return 1;

        // Spline tips are authored around their attachment origin with local Z
        // as their longitudinal axis. The claw's mounting block is the short
        // side of that origin and its prongs are the long side. Infer which
        // local-Z direction is distal from those authored extents instead of
        // assigning an orientation to a character or filename.
        var negativeReach = MathF.Max(0f, -tipMesh.Vertices.Min(static vertex => vertex.Z));
        var positiveReach = MathF.Max(0f, tipMesh.Vertices.Max(static vertex => vertex.Z));
        return negativeReach > positiveReach ? -1 : 1;
    }

    internal static PsxSplineTipPlacement CreateTipPlacement(
        PsxSplineControllerChain chain,
        int forwardSign = 1)
    {
        if (forwardSign is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(forwardSign));

        var centers = BuildSmoothedSamples(chain)
            .Select(static sample => sample.Center)
            .ToArray();
        var last = centers.Length - 1;
        var tangent = GetTangent(centers, last);
        var (normal, binormal) = BuildTransportFrames(centers)[last];
        return new PsxSplineTipPlacement(
            chain.ObjectIndices[^1], centers[last], tangent, normal, binormal, forwardSign);
    }

    /// <summary>
    ///     Adds the frame rotations that the native runtime implicitly gets by
    ///     rebuilding a spline surface from its translated control points each
    ///     tick. The stored controller tracks carry positions but no rotations;
    ///     ordinary glTF skinning would therefore bend the vertices while
    ///     leaving their bind-pose radial normals behind. Deriving a transport
    ///     frame at each key rotates both positions and normals with the curve.
    /// </summary>
    internal static void ApplyGeneratedFrameRotations(
        ModelDocument document,
        int skeletonIndex,
        IReadOnlyList<PsxSplineControllerChain> chains,
        IReadOnlyList<PsxAnimationClip> clips)
    {
        if (chains.Count == 0
            || clips.Count == 0
            || skeletonIndex < 0
            || skeletonIndex >= document.Skeletons.Count)
        {
            return;
        }

        var skeleton = document.Skeletons[skeletonIndex];
        var availableAnimations = document.Animations.ToList();
        foreach (var clip in clips)
        {
            var animationIndex = availableAnimations.FindIndex(animation =>
                string.Equals(animation.Name, clip.Name, StringComparison.Ordinal));
            if (animationIndex < 0)
                continue;

            var modelAnimation = availableAnimations[animationIndex];
            availableAnimations.RemoveAt(animationIndex);
            foreach (var chain in chains)
            {
                if (chain.ObjectIndices.Any(index =>
                        index < 0
                        || index >= skeleton.Bones.Count
                        || index >= clip.Animation.BoneCount
                        || clip.Animation.IsRotationAnimated(index)))
                {
                    continue;
                }

                var parentIndices = chain.ObjectIndices
                    .Select(index => skeleton.Bones[index].ParentIndex)
                    .Distinct()
                    .ToArray();
                if (parentIndices.Length != 1)
                    continue;

                ApplyGeneratedFrameRotations(
                    modelAnimation, skeletonIndex, skeleton, chain);
            }
        }
    }

    private static void ApplyGeneratedFrameRotations(
        ModelAnimation animation,
        int skeletonIndex,
        ModelSkeleton skeleton,
        PsxSplineControllerChain chain)
    {
        var translationChannels = animation.Channels
            .Where(channel => channel.SkeletonIndex == skeletonIndex
                              && channel.Property == ModelAnimationProperty.Translation
                              && chain.ObjectIndices.Contains(channel.BoneIndex))
            .ToDictionary(static channel => channel.BoneIndex);
        var timeline = translationChannels.Values.FirstOrDefault()?.Times;
        if (timeline is not { Length: > 0 }
            || translationChannels.Count != chain.ObjectIndices.Count)
            return;

        var bindFrames = BuildTransportFrames(chain.Centers);
        var valuesByBone = chain.ObjectIndices.ToDictionary(
            static index => index,
            _ => new float[timeline.Length * 4]);
        var previousByBone = chain.ObjectIndices.ToDictionary(
            static index => index,
            static _ => Quaternion.Identity);

        for (var frame = 0; frame < timeline.Length; frame++)
        {
            var currentCenters = new Vector3[chain.ObjectIndices.Count];
            for (var controller = 0; controller < chain.ObjectIndices.Count; controller++)
            {
                var boneIndex = chain.ObjectIndices[controller];
                var gltfPosition = translationChannels.TryGetValue(boneIndex, out var channel)
                    ? SampleTranslation(channel, timeline[frame])
                    : skeleton.Bones[boneIndex].LocalTransform.Translation;
                currentCenters[controller] = PsxMeshSemantics.ToGltfPosition(gltfPosition);
            }

            if (currentCenters.Zip(currentCenters.Skip(1), Vector3.Distance)
                .Any(static distance => distance <= 1e-4f || !float.IsFinite(distance)))
            {
                return;
            }

            var currentFrames = BuildTransportFrames(currentCenters);
            for (var controller = 0; controller < chain.ObjectIndices.Count; controller++)
            {
                var boneIndex = chain.ObjectIndices[controller];
                var bindTangent = GetTangent(chain.Centers, controller);
                var currentTangent = GetTangent(currentCenters, controller);
                var bindMatrix = CreateGltfFrameMatrix(
                    bindFrames[controller], bindTangent);
                var currentMatrix = CreateGltfFrameMatrix(
                    currentFrames[controller], currentTangent);
                var rotationMatrix = Matrix4x4.Transpose(bindMatrix) * currentMatrix;
                var rotation = Quaternion.Normalize(
                    Quaternion.CreateFromRotationMatrix(rotationMatrix));
                if (!float.IsFinite(rotation.X)
                    || !float.IsFinite(rotation.Y)
                    || !float.IsFinite(rotation.Z)
                    || !float.IsFinite(rotation.W))
                {
                    return;
                }

                var previous = previousByBone[boneIndex];
                if (frame > 0 && Quaternion.Dot(previous, rotation) < 0f)
                    rotation = new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);

                var values = valuesByBone[boneIndex];
                var offset = frame * 4;
                values[offset] = rotation.X;
                values[offset + 1] = rotation.Y;
                values[offset + 2] = rotation.Z;
                values[offset + 3] = rotation.W;
                previousByBone[boneIndex] = rotation;
            }
        }

        var controllerObjects = chain.ObjectIndices.ToHashSet();
        animation.Channels.RemoveAll(channel =>
            channel.SkeletonIndex == skeletonIndex
            && channel.Property == ModelAnimationProperty.Rotation
            && controllerObjects.Contains(channel.BoneIndex));
        foreach (var boneIndex in chain.ObjectIndices)
        {
            animation.Channels.Add(new ModelAnimationChannel
            {
                SkeletonIndex = skeletonIndex,
                BoneIndex = boneIndex,
                Property = ModelAnimationProperty.Rotation,
                Times = timeline.ToArray(),
                Values = valuesByBone[boneIndex],
                Interpolation = ModelAnimationInterpolation.Linear
            });
        }
    }

    private static Vector3 SampleTranslation(ModelAnimationChannel channel, float time)
    {
        if (channel.Times.Length == 1 || time <= channel.Times[0])
            return ReadTranslation(channel.Values, 0);

        var last = channel.Times.Length - 1;
        if (time >= channel.Times[last])
            return ReadTranslation(channel.Values, last);

        var upper = Array.BinarySearch(channel.Times, time);
        if (upper >= 0)
            return ReadTranslation(channel.Values, upper);
        upper = ~upper;
        var lower = upper - 1;
        var span = channel.Times[upper] - channel.Times[lower];
        var amount = span > 0f ? (time - channel.Times[lower]) / span : 0f;
        return Vector3.Lerp(
            ReadTranslation(channel.Values, lower),
            ReadTranslation(channel.Values, upper),
            amount);
    }

    private static Vector3 ReadTranslation(float[] values, int index)
    {
        var offset = index * 3;
        return new Vector3(values[offset], values[offset + 1], values[offset + 2]);
    }

    private static Matrix4x4 CreateGltfFrameMatrix(
        (Vector3 Normal, Vector3 Binormal) nativeFrame,
        Vector3 nativeTangent)
    {
        var normal = PsxMeshSemantics.ToGltfPosition(nativeFrame.Normal);
        var binormal = PsxMeshSemantics.ToGltfPosition(nativeFrame.Binormal);
        var tangent = PsxMeshSemantics.ToGltfPosition(nativeTangent);
        return new Matrix4x4(
            normal.X, normal.Y, normal.Z, 0f,
            binormal.X, binormal.Y, binormal.Z, 0f,
            tangent.X, tangent.Y, tangent.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    private static Vector3 GetTangent(IReadOnlyList<Vector3> centers, int index)
    {
        var tangent = index switch
        {
            0 => centers[1] - centers[0],
            _ when index == centers.Count - 1 => centers[index] - centers[index - 1],
            _ => centers[index + 1] - centers[index - 1]
        };
        return Vector3.Normalize(tangent);
    }

    private static (Vector3 Normal, Vector3 Binormal) BuildFrame(Vector3 tangent)
    {
        var reference = MathF.Abs(Vector3.Dot(tangent, Vector3.UnitY)) < 0.9f
            ? Vector3.UnitY
            : Vector3.UnitX;
        var normal = Vector3.Normalize(Vector3.Cross(tangent, reference));
        return (normal, Vector3.Normalize(Vector3.Cross(tangent, normal)));
    }

    /// <summary>
    ///     Builds a rotation-minimizing frame along a controller chain. Each
    ///     normal is projected into the next tangent plane, avoiding the abrupt
    ///     0.9-dot reference-axis switch that otherwise twists the tube and its
    ///     UV seam as an animated tentacle crosses that threshold.
    /// </summary>
    internal static IReadOnlyList<(Vector3 Normal, Vector3 Binormal)> BuildTransportFrames(
        IReadOnlyList<Vector3> centers)
    {
        ArgumentNullException.ThrowIfNull(centers);
        if (centers.Count < 2)
            throw new ArgumentException("At least two centers are required.", nameof(centers));

        var frames = new (Vector3 Normal, Vector3 Binormal)[centers.Count];
        var firstTangent = GetTangent(centers, 0);
        frames[0] = BuildFrame(firstTangent);

        for (var index = 1; index < centers.Count; index++)
        {
            var tangent = GetTangent(centers, index);
            var previous = frames[index - 1];
            var normal = previous.Normal - tangent * Vector3.Dot(previous.Normal, tangent);
            if (normal.LengthSquared() < 1e-8f)
                normal = previous.Binormal - tangent * Vector3.Dot(previous.Binormal, tangent);

            if (normal.LengthSquared() < 1e-8f)
            {
                frames[index] = BuildFrame(tangent);
                continue;
            }

            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, previous.Normal) < 0f)
                normal = -normal;
            frames[index] = (
                normal,
                Vector3.Normalize(Vector3.Cross(tangent, normal)));
        }

        return frames;
    }
}
