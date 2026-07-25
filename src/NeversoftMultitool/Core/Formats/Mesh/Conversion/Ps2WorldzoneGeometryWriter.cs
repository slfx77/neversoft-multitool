using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     THAW PS2 worldzone leaves: per-sector placement, leaf filtering, and
///     billboard/overlay vertex transforms.
/// </summary>
internal static class Ps2WorldzoneGeometryWriter
{
    public static void PopulatePs2Worldzone(
        ModelDocument document,
        byte[] pakBytes,
        string sourceName,
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        ZoneTextureCatalog? textureCatalog,
        string? textureSourceHint,
        WorldzoneTimeOfDay timeOfDay,
        float coordinateScale,
        Ps2WorldzoneLighting? lighting = null)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Worldzone coordinate scale must be a finite positive value.");

        // THAW worldzone MDLs normally do not expose a trusted normal stream.
        // Leave vertex colours as parsed unless a caller explicitly opts into
        // the synthetic worldzone lighting model.
        ModelDocumentGeometryAdapter.ActivePs2WorldzoneLighting = lighting;

        var typedEntries = PakArchive.GetTypedEntries(pakBytes);
        var mdlEntries = typedEntries
            .Where(static entry => entry.TypeHash is
                Ps2WorldzoneDetection.WorldzoneMdlTypeHash or
                Ps2WorldzoneDetection.WorldzoneLevelMdlTypeHash)
            .Select(static entry => entry.Entry)
            .ToList();

        if (mdlEntries.Count == 0)
        {
            ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
            return;
        }

        document.NativeMetadata.Add(new Ps2WorldzoneRenderMetadata(
            sourceName,
            mdlEntries.Count,
            timeOfDay.ToString(),
            coordinateScale));

        var materialCache = new Dictionary<Ps2WorldzoneMaterialWriter.Ps2WorldzoneMaterialKey, int>();
        try
        {
            foreach (var mdlEntry in mdlEntries)
            {
                if (mdlEntry.Offset < 0 ||
                    mdlEntry.Size <= 0 ||
                    mdlEntry.Offset + mdlEntry.Size > pakBytes.Length)
                {
                    continue;
                }

                var mdlData = new byte[mdlEntry.Size];
                Array.Copy(pakBytes, mdlEntry.Offset, mdlData, 0, (int)mdlEntry.Size);
                var mdlName = $"{mdlEntry.Offset:X8}";
                mdlData = Ps2WorldzoneMdlPreamble.ExtendLevelMdlPreambleIfNeeded(pakBytes, mdlEntry, mdlData);
                if (!Ps2GeomFile.IsPakMdl(mdlData))
                    continue;

                var mdlTextureHint = textureCatalog?.FindTextureEntryHintBefore(textureSourceHint, mdlEntry.Offset)
                                     ?? textureSourceHint;
                var mdlTex0Resolver = textureCatalog?.CreateTex0ChecksumResolver(mdlTextureHint)
                                      ?? tex0Resolver;
                var geomScene = Ps2GeomFile.ParsePakMdl(mdlData, mdlName);
                var placements = geomScene.MdlPreamble?.Bones.Count > 0
                    ? Ps2MdlPlacementResolver.ResolveWorldzonePlacements(geomScene.MdlPreamble)
                    : [];

                var rootPlacements = new List<(Vector3 Position, Quaternion Rotation)>(1);
                var bonePlacements = new List<(Vector3 Position, Quaternion Rotation)>();
                if (placements.Count > 0)
                {
                    rootPlacements.Add((placements[0].Position, placements[0].Rotation));
                    bonePlacements.AddRange(placements.Skip(1).Select(static p => (p.Position, p.Rotation)));
                }
                else
                {
                    rootPlacements.Add((Vector3.Zero, Quaternion.Identity));
                }

                PopulatePs2WorldzoneLeaves(
                    document,
                    geomScene,
                    mdlName,
                    rootPlacements,
                    leaf => !leaf.IsLocalSpace && ShouldIncludeWorldzoneLeaf(leaf, timeOfDay),
                    materialCache,
                    textureProvider,
                    texaTextureProvider,
                    mdlTex0Resolver,
                    coordinateScale,
                    "world");

                if (bonePlacements.Count > 0)
                {
                    PopulatePs2WorldzoneLeaves(
                        document,
                        geomScene,
                        mdlName,
                        bonePlacements,
                        leaf => leaf.IsLocalSpace && ShouldIncludeWorldzoneLeaf(leaf, timeOfDay),
                        materialCache,
                        textureProvider,
                        texaTextureProvider,
                        mdlTex0Resolver,
                        coordinateScale,
                        "local");
                }
            }
        }
        finally
        {
            ModelDocumentGeometryAdapter.ActivePs2WorldzoneLighting = null;
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static void PopulatePs2WorldzoneLeaves(
        ModelDocument document,
        Ps2GeomScene scene,
        string mdlName,
        List<(Vector3 Position, Quaternion Rotation)> placements,
        Func<Ps2GeomLeaf, bool> leafFilter,
        Dictionary<Ps2WorldzoneMaterialWriter.Ps2WorldzoneMaterialKey, int> materialCache,
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        float coordinateScale,
        string space)
    {
        var instances = placements.Count > 0
            ? placements
            : [(Vector3.Zero, Quaternion.Identity)];
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw(scene.Leaves);
        var sourceTextureProvider =
            Ps2WorldzoneMaterialWriter.ResolvePs2TexaAwareProvider(textureProvider, texaTextureProvider);
        var syntheticTextures = new Dictionary<uint, byte[]>();
        Ps2TexaTextureResolver? effectiveTexaTextureProvider = sourceTextureProvider == null
            ? null
            : (checksum, texa) => syntheticTextures.TryGetValue(checksum, out var syntheticPng)
                ? syntheticPng
                : sourceTextureProvider(checksum, texa);
        var destinationAlphaMasks = Ps2GeomDestinationAlphaSynthesis.BuildMaskCandidates(
            orderedLeaves,
            sourceTextureProvider,
            tex0Resolver,
            leafFilter,
            ShouldSkipWorldzoneLeaf);
        var recentAlphaMasks = new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>();

        foreach (var drawItem in orderedLeaves)
        {
            var leaf = drawItem.Leaf;
            var leafIndex = drawItem.LeafIndex;
            if (leaf.Vertices.Length < 3 ||
                !leafFilter(leaf) ||
                ShouldSkipWorldzoneLeaf(leaf))
            {
                continue;
            }

            var textureChecksum = Ps2WorldzoneMaterialWriter.ResolvePs2GeomTextureChecksum(leaf, tex0Resolver);
            var geometryKey = Ps2GeomDestinationAlphaSynthesis.CreateLeafGeometryKey(leaf);
            if (Ps2WorldzoneMaterialWriter.ShouldSkipRedundantWorldzoneBlendLayer(leaf, textureChecksum, geometryKey,
                    recentAlphaMasks))
                continue;

            var usesSynthesizedDestinationAlpha = false;
            if (textureChecksum != 0 && effectiveTexaTextureProvider != null &&
                Ps2GeomDestinationAlphaSynthesis.TryCreateSyntheticTexture(
                    leaf,
                    textureChecksum,
                    Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(leaf),
                    destinationAlphaMasks,
                    recentAlphaMasks,
                    effectiveTexaTextureProvider,
                    syntheticTextures,
                    out var syntheticTextureChecksum))
            {
                textureChecksum = syntheticTextureChecksum;
                usesSynthesizedDestinationAlpha = true;
            }

            var alphaModePng = textureChecksum != 0
                ? effectiveTexaTextureProvider?.Invoke(textureChecksum, leaf.DmaTexa)
                : null;
            var alphaMode =
                Ps2MaterialWriter.ClassifyPs2GeomEffectiveAlphaMode(leaf, alphaModePng,
                    usesSynthesizedDestinationAlpha);
            var depthBias = Ps2GeomRenderSemantics.ComputeWorldzoneMaterialDepthBias(leaf, alphaMode);
            // Preserve the shared PS2 group/mode bias formula, then add only a
            // tiny draw-order stagger for coplanar same-group passes that the PS2
            // resolves by submission order.
            const float DrawOrderStaggerBlenderUnits = 0.00000025f;
            var effectiveBias = depthBias > 0f && coordinateScale > 0f
                ? depthBias + drawItem.DrawIndex * DrawOrderStaggerBlenderUnits / coordinateScale
                : depthBias;
            var sourceVertices = effectiveBias > 0f
                ? OffsetPs2Vertices(leaf.Vertices, ComputeOverlayOffsetDirection(leaf.Vertices), effectiveBias)
                : leaf.Vertices;
            var (min, max) = ComputeBbox(sourceVertices);
            var localOrigin = (min + max) * 0.5f;
            var localizedVertices = LocalizePs2Vertices(sourceVertices, localOrigin, coordinateScale);

            var materialIndex = Ps2WorldzoneMaterialWriter.GetOrCreatePs2WorldzoneMaterial(
                document,
                materialCache,
                leaf,
                null,
                effectiveTexaTextureProvider,
                tex0Resolver,
                textureChecksum,
                usesSynthesizedDestinationAlpha,
                alphaMode);
            var preserveVertexAlpha = Ps2SceneGeometryWriter.ShouldPreservePs2GeomVertexAlpha(leaf, alphaMode);

            var emittedLeaf = false;
            for (var placementIndex = 0; placementIndex < instances.Count; placementIndex++)
            {
                var (position, rotation) = instances[placementIndex];
                var mesh = new ModelMesh
                {
                    Name = $"{mdlName}_{space}_leaf_{leafIndex:D5}"
                };
                var primitive = Ps2SceneGeometryWriter.AddPs2StripPrimitive(
                    mesh,
                    "strip",
                    materialIndex,
                    localizedVertices,
                    false,
                    null,
                    preserveVertexAlpha,
                    false);

                if (primitive == null)
                    continue;

                emittedLeaf = true;
                primitive.NativeMetadata.Add(
                    Ps2WorldzoneMaterialWriter.MakePs2GsMetadata(leaf, tex0Resolver, "ps2_worldzone_leaf"));
                primitive.NativeMetadata.Add(new Ps2WorldzoneLeafRenderMetadata(
                    mdlName,
                    leafIndex,
                    space,
                    Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf).ToString(),
                    Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(leaf),
                    leaf.IsBillboard,
                    leaf.IsLocalSpace,
                    leaf.Colour,
                    leaf.Flags));
                if (leaf.BillboardDescriptor is { } billboard)
                {
                    primitive.NativeMetadata.Add(new Ps2WorldzoneBillboardMetadata(
                        billboard.Kind.ToString(),
                        billboard.Anchor.X, billboard.Anchor.Y, billboard.Anchor.Z,
                        billboard.Size.X, billboard.Size.Y,
                        billboard.PivotLocal.X, billboard.PivotLocal.Y, billboard.PivotLocal.Z,
                        billboard.Axis.X, billboard.Axis.Y, billboard.Axis.Z));
                }

                var nodePosition = position + Vector3.Transform(localOrigin, rotation);
                nodePosition *= coordinateScale;
                var nodeName = instances.Count == 1
                    ? mesh.Name
                    : $"{mesh.Name}_p{placementIndex:D4}";
                ModelDocumentGeometryAdapter.AddMeshNode(document, nodeName, mesh,
                    CreateTransform(rotation, nodePosition));
            }

            if (emittedLeaf &&
                textureChecksum != 0 &&
                Ps2GeomRenderSemantics.WritesFramebufferAlpha(leaf) &&
                !Ps2GeomRenderSemantics.UsesDestinationAlphaBlend((byte)(leaf.DmaAlpha1 & 0xFF)))
            {
                recentAlphaMasks[geometryKey] =
                    new Ps2DestinationAlphaMaskCandidate(geometryKey, textureChecksum, leaf);
            }
        }
    }

    private static bool ShouldIncludeWorldzoneLeaf(
        Ps2GeomLeaf leaf,
        WorldzoneTimeOfDay timeOfDay)
    {
        if (timeOfDay is WorldzoneTimeOfDay.All or WorldzoneTimeOfDay.Night)
            return true;

        return Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf) != Ps2GeomRenderLayer.NightOverlay;
    }

    private static bool ShouldSkipWorldzoneLeaf(Ps2GeomLeaf leaf)
    {
        // Format-B billboard leaves used to be quarantined here because the static
        // export had no way to face them at the camera. They now carry a full
        // Ps2BillboardDescriptor and the Blender importer attaches a Track-To
        // constraint per billboard, so they're allowed through.
        if (leaf.IsBillboard)
            return false;

        if (leaf.Vertices.Length < 4)
            return false;

        if (leaf.Vertices.Any(static vertex => vertex.HasNormal))
            return false;

        var (min, max) = ComputeBbox(leaf.Vertices);
        var size = max - min;
        var maxDimension = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (maxDimension < 1000f)
            return false;

        var center = (min + max) * 0.5f;
        if (Math.Abs(center.X) > 10f || Math.Abs(center.Y) > 10f || Math.Abs(center.Z) > 10f)
            return false;

        var restartCount = leaf.Vertices.Count(static vertex => vertex.IsStripRestart);
        return restartCount >= Math.Max(2, leaf.Vertices.Length / 5);
    }

    private static Ps2Vertex[] LocalizePs2Vertices(Ps2Vertex[] vertices, Vector3 origin, float scale)
    {
        if (vertices.Length == 0)
            return vertices;

        var result = new Ps2Vertex[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            result[i] = CopyPs2Vertex(vertex, (vertex.Position - origin) * scale);
        }

        return result;
    }

    private static Ps2Vertex[] OffsetPs2Vertices(Ps2Vertex[] vertices, Vector3 direction, float distance)
    {
        if (vertices.Length == 0 || MathF.Abs(distance) <= 1e-8f || direction.LengthSquared() <= 1e-8f)
            return vertices;

        var offset = direction * distance;
        var result = new Ps2Vertex[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            result[i] = CopyPs2Vertex(vertex, vertex.Position + offset);
        }

        return result;
    }

    private static Ps2Vertex CopyPs2Vertex(Ps2Vertex vertex, Vector3 position)
    {
        return new Ps2Vertex(
            position,
            vertex.Normal,
            vertex.R,
            vertex.G,
            vertex.B,
            vertex.A,
            vertex.U,
            vertex.V,
            vertex.HasNormal,
            vertex.HasColor,
            vertex.HasUV,
            vertex.IsStripRestart,
            vertex.BoneIndex0,
            vertex.BoneIndex1,
            vertex.BoneIndex2,
            vertex.BoneWeight0,
            vertex.BoneWeight1,
            vertex.BoneWeight2,
            vertex.HasSkinData);
    }

    internal static (Vector3 Min, Vector3 Max) ComputeBbox(Ps2Vertex[] vertices)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        // Single-pass indexed min/max — a Select(v => v.Position) projection would
        // allocate an enumerator and still need this same reduction.
        for (var i = 0; i < vertices.Length; i++)
        {
            var position = vertices[i].Position;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return (min, max);
    }

    private static Vector3 ComputeOverlayOffsetDirection(Ps2Vertex[] vertices)
    {
        var normal = Vector3.Zero;
        foreach (var vertex in vertices)
        {
            if (!vertex.HasNormal || vertex.Normal.LengthSquared() <= 1e-8f)
                continue;

            normal += Vector3.Normalize(vertex.Normal);
        }

        if (normal.LengthSquared() <= 1e-8f)
            normal = ComputeStripNormal(vertices);

        if (normal.LengthSquared() <= 1e-8f)
            return Vector3.UnitY;

        normal = Vector3.Normalize(normal);
        return Math.Abs(normal.Y) > 0.5f && normal.Y < 0 ? -normal : normal;
    }

    private static Vector3 ComputeStripNormal(Ps2Vertex[] vertices)
    {
        var normal = Vector3.Zero;
        var stripStart = 0;
        var lastWasRestart = false;

        for (var i = 0; i < vertices.Length; i++)
        {
            if (vertices[i].IsStripRestart)
            {
                if (!lastWasRestart)
                    stripStart = i;
                lastWasRestart = true;
                continue;
            }

            lastWasRestart = false;
            var localIndex = i - stripStart;
            if (localIndex < 2)
                continue;

            var a = (localIndex & 1) == 0 ? vertices[i - 2].Position : vertices[i - 1].Position;
            var b = (localIndex & 1) == 0 ? vertices[i - 1].Position : vertices[i - 2].Position;
            var c = vertices[i].Position;
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() > 1e-8f)
                normal += Vector3.Normalize(cross);
        }

        return normal;
    }

    private static Matrix4x4 CreateTransform(Quaternion rotation, Vector3 translation)
    {
        var transform = Matrix4x4.CreateFromQuaternion(rotation);
        transform.Translation = translation;
        return transform;
    }
}
