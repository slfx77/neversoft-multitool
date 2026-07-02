using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
    private static void PopulatePs2WorldzoneLeaves(
        ModelDocument document,
        Ps2GeomScene scene,
        string mdlName,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation)> placements,
        Func<Ps2GeomLeaf, bool> leafFilter,
        Dictionary<Ps2WorldzoneMaterialKey, int> materialCache,
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
        var sourceTextureProvider = ResolvePs2TexaAwareProvider(textureProvider, texaTextureProvider);
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

            var textureChecksum = ResolvePs2GeomTextureChecksum(leaf, tex0Resolver);
            var geometryKey = Ps2GeomDestinationAlphaSynthesis.CreateLeafGeometryKey(leaf);
            if (ShouldSkipRedundantWorldzoneBlendLayer(leaf, textureChecksum, geometryKey, recentAlphaMasks))
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
            var alphaMode = ClassifyPs2GeomEffectiveAlphaMode(leaf, alphaModePng, usesSynthesizedDestinationAlpha);
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

            var materialIndex = GetOrCreatePs2WorldzoneMaterial(
                document,
                materialCache,
                leaf,
                null,
                effectiveTexaTextureProvider,
                tex0Resolver,
                textureChecksum,
                usesSynthesizedDestinationAlpha,
                alphaMode);
            var preserveVertexAlpha = ShouldPreservePs2GeomVertexAlpha(leaf, alphaMode);

            var emittedLeaf = false;
            for (var placementIndex = 0; placementIndex < instances.Count; placementIndex++)
            {
                var (position, rotation) = instances[placementIndex];
                var mesh = new ModelMesh
                {
                    Name = $"{mdlName}_{space}_leaf_{leafIndex:D5}"
                };
                var primitive = AddPs2StripPrimitive(
                    mesh,
                    "strip",
                    materialIndex,
                    localizedVertices,
                    false,
                    null,
                    true,
                    preserveVertexAlpha,
                    false);

                if (primitive == null)
                    continue;

                emittedLeaf = true;
                primitive.NativeMetadata.Add(MakePs2GsMetadata(leaf, tex0Resolver, "ps2_worldzone_leaf"));
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
                AddMeshNode(document, nodeName, mesh, CreateTransform(rotation, nodePosition));
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

    private static bool ShouldSkipRedundantWorldzoneBlendLayer(
        Ps2GeomLeaf leaf,
        uint textureChecksum,
        Ps2DestinationAlphaLeafGeometryKey geometryKey,
        IReadOnlyDictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate> recentAlphaMasks)
    {
        if (textureChecksum == 0 || leaf.IsBillboard)
            return false;

        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        if (!Ps2GeomRenderSemantics.IsStandardSourceAlphaBlend(alphaBlend))
            return false;

        if (!recentAlphaMasks.TryGetValue(geometryKey, out var previous) ||
            previous.TextureChecksum != textureChecksum)
        {
            return false;
        }

        var previousAlphaBlend = (byte)(previous.Leaf.DmaAlpha1 & 0xFF);
        if (previousAlphaBlend is not (0x0A or 0x1A or 0x00))
            return false;

        var (min, max) = ComputeBbox(leaf.Vertices);
        var size = max - min;
        var maxDimension = Math.Max(Math.Abs(size.X), Math.Max(Math.Abs(size.Y), Math.Abs(size.Z)));
        return maxDimension >= 250f;
    }

    private static bool ShouldPreservePs2SceneVertexAlpha(Ps2Material material)
    {
        var fixedOpacity = material.FixedBlendOpacity;
        return !fixedOpacity.HasValue ||
               fixedOpacity.Value >= Ps2SceneRenderSemantics.FixBlendOpaqueThreshold / 128f;
    }

    private static bool ShouldPreservePs2GeomVertexAlpha(Ps2GeomLeaf leaf, string alphaMode)
    {
        if (!string.Equals(alphaMode, "BLEND", StringComparison.Ordinal))
            return true;

        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        return Ps2GeomRenderSemantics.BlendUsesSourceAlpha(alphaBlend);
    }

    private static int GetOrCreatePs2WorldzoneMaterial(
        ModelDocument document,
        Dictionary<Ps2WorldzoneMaterialKey, int> materialCache,
        Ps2GeomLeaf leaf,
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        uint? textureChecksumOverride = null,
        bool useTextureAlphaMode = false,
        string? alphaModeOverride = null)
    {
        var textureChecksum = textureChecksumOverride ?? ResolvePs2GeomTextureChecksum(leaf, tex0Resolver);
        var alphaModeKey = alphaModeOverride ?? Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf);
        var key = new Ps2WorldzoneMaterialKey(
            textureChecksum,
            leaf.DmaClamp1 & 0x0F,
            leaf.DmaAlpha1,
            leaf.DmaTest1,
            leaf.DmaTexa,
            leaf.GroupChecksum,
            leaf.IsBillboard,
            alphaModeKey);
        if (materialCache.TryGetValue(key, out var existing))
            return existing;

        var materialName = textureChecksum != 0
            ? ResolveQbName(textureChecksum, $"tex_{textureChecksum:X8}")
            : "default";
        var renderMaterial = new RenderMaterial
        {
            Name = $"{materialName}_{materialCache.Count:D5}"
        };
        renderMaterial.NativeMetadata.Add(MakePs2GsMetadata(
            leaf,
            tex0Resolver,
            "ps2_worldzone_material",
            textureChecksum));
        ApplyPs2GeomMaterial(
            document,
            renderMaterial,
            leaf,
            textureProvider,
            tex0Resolver,
            texaTextureProvider,
            textureChecksum,
            useTextureAlphaMode,
            alphaModeOverride);
        var index = AddMaterial(document, renderMaterial);
        materialCache[key] = index;
        return index;
    }

    private static Ps2GsRenderMetadata MakePs2GsMetadata(
        Ps2GeomLeaf leaf,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        string source,
        uint? textureChecksumOverride = null)
    {
        var textureChecksum = textureChecksumOverride ?? ResolvePs2GeomTextureChecksum(leaf, tex0Resolver);
        return new Ps2GsRenderMetadata(
            leaf.DmaAlpha1,
            leaf.DmaTest1,
            leaf.DmaTex0,
            leaf.DmaTex1,
            leaf.DmaTexa,
            leaf.DmaClamp1,
            textureChecksum != 0 ? textureChecksum : null,
            leaf.GroupChecksum,
            (int)((leaf.DmaTest1 >> 4) & 0xFF),
            source,
            leaf.DmaFrame1);
    }

    private static uint ResolvePs2GeomTextureChecksum(
        Ps2GeomLeaf leaf,
        Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        return leaf.TextureChecksum != 0
            ? leaf.TextureChecksum
            : tex0Resolver?.Invoke(leaf.DmaTex0, leaf.GroupChecksum) ?? 0;
    }

    private static Ps2TexaTextureResolver? ResolvePs2TexaAwareProvider(
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider)
    {
        if (texaTextureProvider != null)
            return texaTextureProvider;
        if (textureProvider == null)
            return null;
        return (checksum, _) => textureProvider(checksum);
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
        if (vertices.Length == 0 || distance == 0 || direction.LengthSquared() <= 1e-8f)
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

    private static (Vector3 Min, Vector3 Max) ComputeBbox(IReadOnlyList<Ps2Vertex> vertices)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
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

    // THAW level-MDL preamble repair. Some entries end in the middle of a
    // 0x50-byte preamble record (z_sm's COL entry starts with the continuation),
    // and those records point back at valid VIF chunks before the root node. The
    // game reads the contiguous PAK bytes; our extracted entry slice must do the
    // same. Lifted out of the now-deleted Ps2WorldzoneConverter as a private
    // adapter helper because PopulatePs2Worldzone is the only caller.
    private const uint MdlPreambleRecordSignature = 0x4B189680;
    private const int MdlPreambleRecordSize = 0x50;
    private const int MdlPreambleRecordSignatureOffset = 0x18;
    private const int MinLevelMdlPreambleRecords = 100;
    private const int MaxLevelMdlPreambleExtensionBytes = 0x4000;

    private static byte[] ExtendLevelMdlPreambleIfNeeded(
        byte[] pakBytes,
        ArchiveEntry mdlEntry,
        byte[] mdlData)
    {
        var preambleStart = FindMdlPreambleStart(mdlData);
        if (preambleStart < 0)
            return mdlData;

        var fullRecordEnd = preambleStart;
        var existingRecords = 0;
        while (fullRecordEnd + MdlPreambleRecordSize <= mdlData.Length
               && ReadUInt32(mdlData, fullRecordEnd + MdlPreambleRecordSignatureOffset) ==
               MdlPreambleRecordSignature)
        {
            existingRecords++;
            fullRecordEnd += MdlPreambleRecordSize;
        }

        if (existingRecords < MinLevelMdlPreambleRecords)
            return mdlData;

        var trailingBytes = mdlData.Length - fullRecordEnd;
        if (trailingBytes <= 0 || trailingBytes >= MdlPreambleRecordSize)
            return mdlData;

        var logicalBase = checked((int)mdlEntry.Offset);
        var maxLogicalLength = Math.Min(
            pakBytes.Length - logicalBase,
            mdlData.Length + MaxLevelMdlPreambleExtensionBytes);

        var extendedEnd = fullRecordEnd;
        var addedRecords = 0;
        while (extendedEnd + MdlPreambleRecordSize <= maxLogicalLength
               && ReadUInt32(pakBytes, logicalBase + extendedEnd + MdlPreambleRecordSignatureOffset) ==
               MdlPreambleRecordSignature)
        {
            addedRecords++;
            extendedEnd += MdlPreambleRecordSize;
        }

        if (addedRecords == 0 || extendedEnd <= mdlData.Length)
            return mdlData;

        var addedLeafRefsIntoMdl = 0;
        for (var recordOffset = fullRecordEnd;
             recordOffset + MdlPreambleRecordSize <= extendedEnd;
             recordOffset += MdlPreambleRecordSize)
        {
            var flags = ReadUInt32(pakBytes, logicalBase + recordOffset + 0x3C);
            if ((flags & 0x2u) == 0)
                continue;

            var field40 = ReadUInt32(pakBytes, logicalBase + recordOffset + 0x40);
            if (field40 < mdlData.Length)
                addedLeafRefsIntoMdl++;
        }

        if (addedLeafRefsIntoMdl == 0)
            return mdlData;

        var extended = new byte[extendedEnd];
        Array.Copy(pakBytes, logicalBase, extended, 0, extended.Length);
        return extended;
    }

    private static int FindMdlPreambleStart(byte[] data)
    {
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            if (ReadUInt32(data, i) != MdlPreambleRecordSignature)
                continue;

            return i >= MdlPreambleRecordSignatureOffset
                ? i - MdlPreambleRecordSignatureOffset
                : -1;
        }

        return -1;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BitConverter.ToUInt32(data, offset);
    }
}
