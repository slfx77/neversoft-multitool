using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static class ModelDocumentGeometryAdapter
{
    private const float DdmDecalNormalOffset = 0.1f;

    /// <summary>
    ///     Optional per-vertex PS2 worldzone lighting model. Null by default so
    ///     worldzone exports pass source vertex colours through unchanged; callers
    ///     can provide a value to bake an experimental ambient + N·L_sun model.
    /// </summary>
    [SuppressMessage("Performance", "CA1810",
        Justification = "intentional global; thread-safety asserted by single-threaded IR build")]
    [ThreadStatic]
    private static Ps2WorldzoneConverter.Ps2WorldzoneLighting? _activePs2WorldzoneLighting;

    public static void PopulateCollision(ModelDocument document, ColScene scene)
    {
        var materialIndex = document.Materials.Count;
        document.Materials.Add(new RenderMaterial
        {
            Name = "collision",
            BaseColor = new Vector4(0.7f, 0.7f, 0.7f, 1f)
        });

        var mesh = new ModelMesh { Name = "collision" };
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();

        foreach (var obj in scene.Objects)
        {
            foreach (var face in obj.Faces)
            {
                if (face.V0 >= obj.Vertices.Length ||
                    face.V1 >= obj.Vertices.Length ||
                    face.V2 >= obj.Vertices.Length)
                {
                    continue;
                }

                AddTriangle(
                    vertices,
                    indices,
                    MakeCollisionVertex(obj, face.V0),
                    MakeCollisionVertex(obj, face.V1),
                    MakeCollisionVertex(obj, face.V2));
            }
        }

        AddPrimitive(mesh, "collision", materialIndex, vertices, indices);
        AddMeshNode(document, "collision", mesh);
        FinalizeTriangleCount(document);
    }

    public static void PopulateDdm(
        ModelDocument document,
        DdmFile ddm,
        Dictionary<string, byte[]>? ddxTextures,
        List<string>? textureDirs = null)
    {
        textureDirs ??= [];
        var materialBase = 0;
        foreach (var obj in ddm.Objects)
        {
            var mesh = new ModelMesh { Name = obj.Name };
            for (var splitIndex = 0; splitIndex < obj.Splits.Count; splitIndex++)
            {
                var split = obj.Splits[splitIndex];
                if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                    continue;

                var material = obj.Materials[split.MaterialIndex];
                var materialIndex = materialBase + split.MaterialIndex;
                if (materialIndex >= 0 && materialIndex < document.Materials.Count)
                    ApplyDdmMaterial(document, document.Materials[materialIndex], material, ddxTextures, textureDirs);

                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                var end = Math.Min(obj.Indices.Length, split.IndexOffset + split.IndexCount);
                for (var i = split.IndexOffset; i + 2 < end; i++)
                {
                    var ai = obj.Indices[i];
                    var bi = obj.Indices[i + 1];
                    var ci = obj.Indices[i + 2];
                    if (ai == bi || ai == ci || bi == ci ||
                        ai >= obj.Vertices.Count ||
                        bi >= obj.Vertices.Count ||
                        ci >= obj.Vertices.Count)
                    {
                        continue;
                    }

                    var va = MakeDdmVertex(obj.Vertices[ai]);
                    var vb = MakeDdmVertex(obj.Vertices[bi]);
                    var vc = MakeDdmVertex(obj.Vertices[ci]);
                    if ((i - split.IndexOffset) % 2 == 0)
                        AddTriangle(vertices, indices, va, vb, vc);
                    else
                        AddTriangle(vertices, indices, vb, va, vc);
                }

                AddPrimitive(mesh, $"split_{splitIndex:D3}", materialIndex, vertices, indices);
            }

            AddMeshNode(document, obj.Name, mesh);
            materialBase += obj.Materials.Count;
        }

        FinalizeTriangleCount(document);
    }

    public static void PopulateDdmPlacedLevel(
        ModelDocument document,
        DdmFile levelDdm,
        PsxLayoutFile? levelPsx,
        DdmFile? objectsDdm,
        PsxLayoutFile? objectsPsx,
        Dictionary<string, byte[]>? ddxTextures,
        List<string>? textureDirs = null)
    {
        textureDirs ??= [];
        PopulateDdmWithLayout(document, levelDdm, levelPsx, ddxTextures, textureDirs, "level");
        if (objectsDdm != null)
            PopulateDdmWithLayout(document, objectsDdm, objectsPsx, ddxTextures, textureDirs, "objects");

        FinalizeTriangleCount(document);
    }

    public static void PopulatePsx(
        ModelDocument document,
        PsxMeshFile psxFile,
        MeshChecksumTextureResolver? textureProvider)
    {
        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache = new Dictionary<(uint Hash, bool SemiTransparent), int>();
        var untexturedMaterial = AddMaterial(document, new RenderMaterial
        {
            Name = "untextured",
            BaseColor = new Vector4(0.7f, 0.7f, 0.7f, 1f)
        });

        var lodVariants = BuildPsxLodVariantSet(psxFile);
        if (UsesCombinedPsxCharacterAssembly(psxFile))
        {
            for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
            {
                var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
                if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count || lodVariants.Contains(meshIndex))
                    continue;

                var transform = Matrix4x4.CreateTranslation(PsxMeshSemantics.ToGltfPosition(
                    PsxMeshSemantics.GetObjectOffset(psxFile.Objects[objectIndex], psxFile.TranslationDivisor)));
                PopulatePsxMeshNode(
                    document,
                    psxFile,
                    meshIndex,
                    $"object_{objectIndex:D3}",
                    transform,
                    materialCache,
                    textureDims,
                    untexturedMaterial,
                    textureProvider);
            }
        }
        else
        {
            for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
            {
                var obj = psxFile.Objects[objectIndex];
                if (obj.MeshIndex >= psxFile.Meshes.Count)
                    continue;

                var transform = Matrix4x4.CreateTranslation(new Vector3(
                    obj.X(psxFile.TranslationDivisor),
                    -obj.Y(psxFile.TranslationDivisor),
                    -obj.Z(psxFile.TranslationDivisor)));
                PopulatePsxMeshNode(
                    document,
                    psxFile,
                    obj.MeshIndex,
                    $"object_{objectIndex:D3}",
                    transform,
                    materialCache,
                    textureDims,
                    untexturedMaterial,
                    textureProvider);
            }
        }

        FinalizeTriangleCount(document);
    }

    public static void PopulatePs2Scene(
        ModelDocument document,
        ParsedPs2Scene scene,
        MeshChecksumTextureResolver? textureProvider)
    {
        var materialMap = new Dictionary<uint, int>();
        var nativeMaterials = new Dictionary<uint, Ps2Material>();
        foreach (var material in scene.Materials)
            nativeMaterials.TryAdd(material.Checksum, material);
        for (var i = 0; i < scene.Materials.Count && i < document.Materials.Count; i++)
        {
            materialMap[scene.Materials[i].Checksum] = i;
            ApplyPs2Material(document, document.Materials[i], scene.Materials[i], textureProvider);
        }

        var dedupByMaterial = new Dictionary<uint, HashSet<(Vector3, Vector3, Vector3)>>();
        foreach (var group in scene.MeshGroups)
        {
            var groupName = ResolveQbName(group.Checksum, $"group_{group.Checksum:X8}");
            foreach (var nativeMesh in group.Meshes)
            {
                if (nativeMesh.Vertices.Length < 3)
                    continue;

                if (!materialMap.TryGetValue(nativeMesh.MaterialChecksum, out var materialIndex))
                    materialIndex = AddMaterial(document, new RenderMaterial
                    {
                        Name = ResolveQbName(nativeMesh.MaterialChecksum, $"mat_{nativeMesh.MaterialChecksum:X8}")
                    });

                if (!dedupByMaterial.TryGetValue(nativeMesh.MaterialChecksum, out var dedup))
                {
                    dedup = [];
                    dedupByMaterial[nativeMesh.MaterialChecksum] = dedup;
                }

                var mesh = new ModelMesh { Name = groupName };
                var preserveVertexAlpha =
                    !nativeMaterials.TryGetValue(nativeMesh.MaterialChecksum, out var nativeMaterial) ||
                    ShouldPreservePs2SceneVertexAlpha(nativeMaterial);
                AddPs2StripPrimitive(
                    mesh,
                    "strip",
                    materialIndex,
                    nativeMesh.Vertices,
                    nativeMesh.StartsOnOddOutputSlot,
                    dedup,
                    false,
                    preserveVertexAlpha,
                    false);
                AddMeshNode(document, groupName, mesh);
            }
        }

        FinalizeTriangleCount(document);
    }

    public static void PopulatePs2Geom(
        ModelDocument document,
        Ps2GeomScene scene,
        MeshChecksumTextureResolver? textureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        for (var leafIndex = 0; leafIndex < scene.Leaves.Count; leafIndex++)
        {
            var leaf = scene.Leaves[leafIndex];
            if (leaf.Vertices.Length < 3)
                continue;

            var materialIndex = leafIndex < document.Materials.Count
                ? leafIndex
                : AddMaterial(document, new RenderMaterial { Name = $"leaf_{leafIndex:D5}" });
            ApplyPs2GeomMaterial(document, document.Materials[materialIndex], leaf, textureProvider, tex0Resolver);

            var mesh = new ModelMesh { Name = $"leaf_{leafIndex:D5}" };
            var alphaMode = Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf);
            var preserveVertexAlpha = ShouldPreservePs2GeomVertexAlpha(leaf, alphaMode);
            AddPs2StripPrimitive(
                mesh,
                "strip",
                materialIndex,
                leaf.Vertices,
                false,
                null,
                true,
                preserveVertexAlpha,
                false);
            AddMeshNode(document, mesh.Name, mesh);
        }

        FinalizeTriangleCount(document);
    }

    public static void PopulatePs2Worldzone(
        ModelDocument document,
        byte[] pakBytes,
        string sourceName,
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        ZoneTextureCatalog? textureCatalog,
        string? textureSourceHint,
        Ps2WorldzoneConverter.WorldzoneTimeOfDay timeOfDay,
        float coordinateScale,
        Ps2WorldzoneConverter.Ps2WorldzoneLighting? lighting = null)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Worldzone coordinate scale must be a finite positive value.");

        // THAW worldzone MDLs normally do not expose a trusted normal stream.
        // Leave vertex colours as parsed unless a caller explicitly opts into
        // the synthetic worldzone lighting model.
        _activePs2WorldzoneLighting = lighting;

        var typedEntries = PakArchive.GetTypedEntries(pakBytes);
        var mdlEntries = typedEntries
            .Where(static entry => entry.TypeHash is
                Ps2WorldzoneConverter.WorldzoneMdlTypeHash or
                Ps2WorldzoneConverter.WorldzoneLevelMdlTypeHash)
            .Select(static entry => entry.Entry)
            .ToList();

        if (mdlEntries.Count == 0)
        {
            FinalizeTriangleCount(document);
            return;
        }

        document.NativeMetadata.Add(new Ps2WorldzoneRenderMetadata(
            sourceName,
            mdlEntries.Count,
            timeOfDay.ToString(),
            coordinateScale));

        var materialCache = new Dictionary<Ps2WorldzoneMaterialKey, int>();
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
                mdlData = Ps2WorldzoneConverter.ExtendLevelMdlPreambleIfNeeded(
                    pakBytes,
                    mdlEntry,
                    mdlData,
                    mdlName,
                    null);
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
            _activePs2WorldzoneLighting = null;
        }

        FinalizeTriangleCount(document);
    }

    public static void PopulateXbxScene(
        ModelDocument document,
        ParsedXbxScene scene,
        MeshChecksumTextureResolver? textureProvider,
        float coordinateScale = 1f)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Coordinate scale must be a finite positive number.");

        var materialMap = new Dictionary<uint, int>();
        for (var i = 0; i < scene.Materials.Length && i < document.Materials.Count; i++)
        {
            materialMap[scene.Materials[i].Checksum] = i;
            ApplyXbxMaterial(document, document.Materials[i], scene.Materials[i], textureProvider);
        }

        foreach (var sector in scene.Sectors)
        {
            foreach (var xbxMesh in sector.Meshes)
            {
                if (xbxMesh.Vertices.Length < 3 || xbxMesh.FaceIndices.Length < 3)
                    continue;

                if (!materialMap.TryGetValue(xbxMesh.MaterialChecksum, out var materialIndex))
                    materialIndex = AddMaterial(document, new RenderMaterial
                    {
                        Name = ResolveQbName(xbxMesh.MaterialChecksum, $"mat_{xbxMesh.MaterialChecksum:X8}")
                    });

                var mesh = new ModelMesh { Name = $"sector_{sector.Checksum:X8}" };
                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                if (xbxMesh.IsPreTriangulated)
                    AddXbxIndexedTriangles(vertices, indices, xbxMesh, coordinateScale);
                else
                    AddXbxTriangleStrip(vertices, indices, xbxMesh, coordinateScale);

                AddPrimitive(mesh, "triangles", materialIndex, vertices, indices);
                AddMeshNode(document, mesh.Name, mesh);
            }
        }

        FinalizeTriangleCount(document);
    }

    public static void PopulateRwDff(
        ModelDocument document,
        RwDffClump clump,
        MeshNamedTextureResolver? textureProvider)
    {
        var materialMap = new Dictionary<(int Geometry, int Material), int>();
        for (var geometryIndex = 0; geometryIndex < clump.Geometries.Length; geometryIndex++)
        {
            var geometry = clump.Geometries[geometryIndex];
            for (var materialIndex = 0; materialIndex < geometry.Materials.Length; materialIndex++)
            {
                materialMap[(geometryIndex, materialIndex)] =
                    AddRwMaterial(document, geometry.Materials[materialIndex], textureProvider, false);
            }
        }

        var frameWorld = BuildRwFrameWorldTransforms(clump.Frames);
        foreach (var atomic in clump.Atomics)
        {
            if (atomic.GeometryIndex < 0 || atomic.GeometryIndex >= clump.Geometries.Length)
                continue;

            var geometry = clump.Geometries[atomic.GeometryIndex];
            if (geometry.Vertices.Length == 0 || geometry.Triangles.Length == 0)
                continue;

            var mesh = new ModelMesh { Name = $"geom_{atomic.GeometryIndex}" };
            foreach (var group in geometry.Triangles.GroupBy(static tri => tri.MaterialIndex))
            {
                var materialIndex = materialMap.TryGetValue((atomic.GeometryIndex, group.Key), out var mapped)
                    ? mapped
                    : AddMaterial(document, new RenderMaterial { Name = "__default__" });
                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                foreach (var tri in group)
                {
                    AddTriangle(
                        vertices,
                        indices,
                        MakeRwVertex(geometry, tri.V0),
                        MakeRwVertex(geometry, tri.V1),
                        MakeRwVertex(geometry, tri.V2));
                }

                AddPrimitive(mesh, $"mat_{group.Key:D3}", materialIndex, vertices, indices);
            }

            var transform = atomic.FrameIndex >= 0 && atomic.FrameIndex < frameWorld.Length
                ? frameWorld[atomic.FrameIndex]
                : Matrix4x4.Identity;
            AddMeshNode(document, $"atomic_{atomic.GeometryIndex}", mesh, transform);
        }

        FinalizeTriangleCount(document);
    }

    public static void PopulateRwBsp(
        ModelDocument document,
        RwBspWorld world,
        MeshNamedTextureResolver? textureProvider)
    {
        for (var i = 0; i < world.Materials.Length && i < document.Materials.Count; i++)
            ApplyRwMaterial(document, document.Materials[i], world.Materials[i], textureProvider, true);

        var mesh = new ModelMesh { Name = "level" };
        foreach (var group in world.Sections
                     .SelectMany(section => section.Triangles.Select(tri => (section, tri)))
                     .GroupBy(item => item.section.MatListWindowBase + item.tri.MaterialIndex))
        {
            var materialIndex = group.Key;
            if (materialIndex < 0 || materialIndex >= world.Materials.Length)
                continue;

            var rwMaterial = world.Materials[materialIndex];
            if (string.IsNullOrEmpty(rwMaterial.TextureName) ||
                IsRwDevTexture(Path.GetFileNameWithoutExtension(rwMaterial.TextureName)))
            {
                continue;
            }

            if (materialIndex >= document.Materials.Count)
                materialIndex = AddRwMaterial(document, rwMaterial, textureProvider, true);

            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var (section, tri) in group)
            {
                AddTriangle(
                    vertices,
                    indices,
                    MakeRwBspVertex(section, tri.V0),
                    MakeRwBspVertex(section, tri.V1),
                    MakeRwBspVertex(section, tri.V2));
            }

            AddPrimitive(mesh, $"mat_{group.Key:D3}", materialIndex, vertices, indices);
        }

        AddMeshNode(document, "world", mesh);
        FinalizeTriangleCount(document);
    }

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
        Ps2WorldzoneConverter.WorldzoneTimeOfDay timeOfDay)
    {
        if (timeOfDay is Ps2WorldzoneConverter.WorldzoneTimeOfDay.All or
            Ps2WorldzoneConverter.WorldzoneTimeOfDay.Night)
        {
            return true;
        }

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

    private static void PopulateDdmWithLayout(
        ModelDocument document,
        DdmFile ddm,
        PsxLayoutFile? psx,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs,
        string nodePrefix)
    {
        if (psx == null)
        {
            for (var objectIndex = 0; objectIndex < ddm.Objects.Count; objectIndex++)
            {
                var obj = ddm.Objects[objectIndex];
                var mesh = BuildDdmObjectMesh(document, obj, ddxTextures, textureDirs);
                AddMeshNode(document, $"{nodePrefix}_{obj.Name}", mesh);
            }

            return;
        }

        var ddmByHash = DdmHashLookup.Build(ddm);
        var meshSlotToDdm = DdmHashLookup.ResolveMeshIndices(psx, ddmByHash);
        var placedIndices = new HashSet<int>();
        var meshCache = new Dictionary<int, int>();

        foreach (var psxObj in psx.Objects)
        {
            if (!meshSlotToDdm.TryGetValue(psxObj.MeshIndex, out var ddmIndex) ||
                (uint)ddmIndex >= (uint)ddm.Objects.Count)
            {
                continue;
            }

            placedIndices.Add(ddmIndex);
            if (!meshCache.TryGetValue(ddmIndex, out var meshIndex))
            {
                var mesh = BuildDdmObjectMesh(document, ddm.Objects[ddmIndex], ddxTextures, textureDirs);
                var addedIndex = AddMesh(document, mesh);
                if (!addedIndex.HasValue)
                    continue;

                meshIndex = addedIndex.Value;
                meshCache[ddmIndex] = meshIndex;
            }

            AddMeshNode(
                document,
                $"{nodePrefix}_{ddm.Objects[ddmIndex].Name}_{psxObj.MeshIndex:D4}",
                meshIndex,
                Matrix4x4.CreateTranslation(new Vector3(-psxObj.X, -psxObj.Y, psxObj.Z)));
        }

        for (var objectIndex = 0; objectIndex < ddm.Objects.Count; objectIndex++)
        {
            if (placedIndices.Contains(objectIndex))
                continue;

            var obj = ddm.Objects[objectIndex];
            var mesh = BuildDdmObjectMesh(document, obj, ddxTextures, textureDirs);
            AddMeshNode(document, $"{nodePrefix}_{obj.Name}", mesh);
        }
    }

    private static ModelMesh BuildDdmObjectMesh(
        ModelDocument document,
        DdmObject obj,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs)
    {
        var mesh = new ModelMesh { Name = obj.Name };
        if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
            return mesh;

        var materialIndices = AddDdmObjectMaterials(document, obj, ddxTextures, textureDirs);
        var minExtent = Math.Min(obj.BBoxExtentX, Math.Min(obj.BBoxExtentY, obj.BBoxExtentZ));
        var isFlat = minExtent < 1.5f;
        var drawOrderRanks = BuildDdmDrawOrderRanks(obj);

        for (var splitIndex = 0; splitIndex < obj.Splits.Count; splitIndex++)
        {
            var split = obj.Splits[splitIndex];
            if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                continue;

            var mat = obj.Materials[split.MaterialIndex];
            var rank = drawOrderRanks.GetValueOrDefault(mat.DrawOrder);
            var drawOrderOffset = rank * DdmDecalNormalOffset;
            var materialOffset = isFlat || mat.BlendMode != 0 ? DdmDecalNormalOffset : 0f;
            var normalOffset = Math.Max(drawOrderOffset, materialOffset);

            AddDdmStripPrimitive(
                mesh,
                $"split_{splitIndex:D3}",
                materialIndices[split.MaterialIndex],
                obj,
                split,
                normalOffset);
        }

        return mesh;
    }

    private static int[] AddDdmObjectMaterials(
        ModelDocument document,
        DdmObject obj,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs)
    {
        var materialIndices = new int[obj.Materials.Count];
        for (var i = 0; i < obj.Materials.Count; i++)
        {
            var material = obj.Materials[i];
            var renderMaterial = new RenderMaterial { Name = material.Name };
            renderMaterial.NativeMetadata.Add(new DdmBlendRenderMetadata(
                material.BlendMode,
                material.DrawOrder,
                material.TextureName,
                material.DiffuseR,
                material.DiffuseG,
                material.DiffuseB,
                material.DiffuseA));
            ApplyDdmMaterial(document, renderMaterial, material, ddxTextures, textureDirs);
            materialIndices[i] = AddMaterial(document, renderMaterial);
        }

        return materialIndices;
    }

    private static void AddDdmStripPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        DdmObject obj,
        DdmSplit split,
        float normalOffset)
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        var end = Math.Min(obj.Indices.Length, split.IndexOffset + split.IndexCount);

        for (var i = split.IndexOffset; i + 2 < end; i++)
        {
            var ai = obj.Indices[i];
            var bi = obj.Indices[i + 1];
            var ci = obj.Indices[i + 2];
            if (ai == bi || ai == ci || bi == ci ||
                ai >= obj.Vertices.Count ||
                bi >= obj.Vertices.Count ||
                ci >= obj.Vertices.Count)
            {
                continue;
            }

            var va = MakeDdmVertex(obj.Vertices[ai], normalOffset);
            var vb = MakeDdmVertex(obj.Vertices[bi], normalOffset);
            var vc = MakeDdmVertex(obj.Vertices[ci], normalOffset);
            if ((i - split.IndexOffset) % 2 == 0)
                AddTriangle(vertices, indices, va, vb, vc);
            else
                AddTriangle(vertices, indices, vb, va, vc);
        }

        AddPrimitive(mesh, name, materialIndex, vertices, indices);
    }

    private static Dictionary<uint, int> BuildDdmDrawOrderRanks(DdmObject obj)
    {
        var ranks = new Dictionary<uint, int>();
        foreach (var drawOrder in obj.Splits
                     .Select(split => split.MaterialIndex)
                     .Where(materialIndex => materialIndex < obj.Materials.Count)
                     .Select(materialIndex => obj.Materials[materialIndex].DrawOrder)
                     .Distinct()
                     .Order())
        {
            ranks.Add(drawOrder, ranks.Count);
        }

        return ranks;
    }

    private static void PopulatePsxMeshNode(
        ModelDocument document,
        PsxMeshFile psxFile,
        int meshIndex,
        string nodeName,
        Matrix4x4 transform,
        Dictionary<(uint Hash, bool SemiTransparent), int> materialCache,
        Dictionary<uint, (int Width, int Height)> textureDims,
        int untexturedMaterial,
        MeshChecksumTextureResolver? textureProvider)
    {
        var psxMesh = psxFile.Meshes[meshIndex];
        if (psxMesh.Faces.Count == 0)
            return;

        var mesh = new ModelMesh { Name = ResolvePsxMeshName(psxFile, meshIndex) };
        foreach (var group in psxMesh.Faces.GroupBy(face =>
                     face.IsTextured && face.TextureHash != 0
                         ? (Hash: face.TextureHash, SemiTransparent: face.IsSemiTransparent)
                         : (Hash: 0u, SemiTransparent: false)))
        {
            var materialIndex = group.Key.Hash == 0
                ? untexturedMaterial
                : GetOrCreatePsxMaterial(
                    document,
                    group.Key.Hash,
                    group.Key.SemiTransparent,
                    textureProvider,
                    textureDims,
                    materialCache);

            var texDims = group.Key.Hash != 0 && textureDims.TryGetValue(group.Key.Hash, out var dims)
                ? dims
                : (Width: 256, Height: 256);
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var face in group)
                AddPsxFace(vertices, indices, psxFile.Version, psxMesh, face, psxFile.GouraudPalette, texDims);

            AddPrimitive(mesh, $"mat_{materialIndex:D3}", materialIndex, vertices, indices);
        }

        AddMeshNode(document, nodeName, mesh, transform);
    }

    private static void AddPsxFace(
        List<ModelVertex> vertices,
        List<int> indices,
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        Vector4[]? gouraudPalette,
        (int Width, int Height) texDims)
    {
        var (c0, c1, c2, c3) = ComputePsxFaceColors(version, face, gouraudPalette);
        var v0 = MakePsxVertex(version, mesh, face, 0, c0, texDims);
        var v1 = MakePsxVertex(version, mesh, face, 1, c1, texDims);
        var v2 = MakePsxVertex(version, mesh, face, 2, c2, texDims);
        AddTriangle(vertices, indices, v0, v1, v2);

        if (face.IsQuad)
        {
            var v3 = MakePsxVertex(version, mesh, face, 3, c3, texDims);
            AddTriangle(vertices, indices, v1, v3, v2);
        }
    }

    private static ModelPrimitive? AddPs2StripPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        Ps2Vertex[] sourceVertices,
        bool startsOnOddOutputSlot,
        HashSet<(Vector3, Vector3, Vector3)>? dedup,
        bool resetOnRestart,
        bool preserveVertexAlpha,
        bool bakeVertexColorsToWhite)
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        var parityBias = startsOnOddOutputSlot ? 1 : 0;

        // Walk the strip and collect emitted triangles. When the source stream
        // has no normals, use the triangle's own face normal as a fallback; do
        // not smooth across strip positions because THAW worldzone leaves often
        // combine coplanar decals and sharp-edged pieces in one strip.
        var triangles = new List<(int A, int B, int C, Vector3 Normal)>();
        var stripStart = 0;

        for (var i = 0; i < sourceVertices.Length; i++)
        {
            var c = sourceVertices[i];
            var localIndex = i - stripStart;

            if (c.IsStripRestart)
            {
                // PS2 GS ADC semantics: vertex stays in the running strip but
                // the triangle ending at it is suppressed. Strip continues —
                // see the end-of-method commentary on the missing-triangle
                // bug if you're tempted to bring back resetOnRestart.
                continue;
            }

            if (localIndex < 2)
                continue;

            int aIdx, bIdx;
            if (((localIndex + parityBias) & 1) == 0)
            {
                aIdx = i - 2;
                bIdx = i - 1;
            }
            else
            {
                aIdx = i - 1;
                bIdx = i - 2;
            }

            var cIdx = i;

            var aPos = sourceVertices[aIdx].Position;
            var bPos = sourceVertices[bIdx].Position;
            var cPos = sourceVertices[cIdx].Position;

            if (IsDegenerate(aPos, bPos, cPos))
                continue;

            if (dedup is not null)
            {
                var key = SortedTriangleKey(aPos, bPos, cPos);
                if (!dedup.Add(key))
                    continue;
            }

            var faceNormal = Vector3.Cross(bPos - aPos, cPos - aPos);
            faceNormal = faceNormal.LengthSquared() > 1e-12f
                ? Vector3.Normalize(faceNormal)
                : Vector3.Zero;
            triangles.Add((aIdx, bIdx, cIdx, faceNormal));
        }

        foreach (var (aIdx, bIdx, cIdx, faceNormal) in triangles)
        {
            var fallbackNormal = faceNormal.LengthSquared() > 0f ? faceNormal : (Vector3?)null;
            AddTriangle(
                vertices,
                indices,
                MakePs2Vertex(sourceVertices[aIdx], preserveVertexAlpha, bakeVertexColorsToWhite, fallbackNormal),
                MakePs2Vertex(sourceVertices[bIdx], preserveVertexAlpha, bakeVertexColorsToWhite, fallbackNormal),
                MakePs2Vertex(sourceVertices[cIdx], preserveVertexAlpha, bakeVertexColorsToWhite, fallbackNormal));
        }

        return AddPrimitive(mesh, name, materialIndex, vertices, indices);
    }

    private static void AddXbxIndexedTriangles(
        List<ModelVertex> vertices,
        List<int> indices,
        XbxMesh mesh,
        float coordinateScale)
    {
        for (var i = 0; i + 2 < mesh.FaceIndices.Length; i += 3)
        {
            var i0 = mesh.FaceIndices[i];
            var i1 = mesh.FaceIndices[i + 1];
            var i2 = mesh.FaceIndices[i + 2];
            if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length)
                continue;
            AddTriangle(
                vertices,
                indices,
                MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
        }
    }

    private static void AddXbxTriangleStrip(
        List<ModelVertex> vertices,
        List<int> indices,
        XbxMesh mesh,
        float coordinateScale)
    {
        for (var i = 2; i < mesh.FaceIndices.Length; i++)
        {
            var i0 = mesh.FaceIndices[i - 2];
            var i1 = mesh.FaceIndices[i - 1];
            var i2 = mesh.FaceIndices[i];
            if (i0 == i1 || i1 == i2 || i0 == i2 ||
                i0 >= mesh.Vertices.Length ||
                i1 >= mesh.Vertices.Length ||
                i2 >= mesh.Vertices.Length)
            {
                continue;
            }

            if (i % 2 == 0)
            {
                AddTriangle(
                    vertices,
                    indices,
                    MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
            }
            else
            {
                AddTriangle(
                    vertices,
                    indices,
                    MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
            }
        }
    }

    private static ModelVertex MakeCollisionVertex(ColObject obj, int index)
    {
        var intensity = index < obj.Intensities.Length ? obj.Intensities[index] / 255f : 1f;
        return new ModelVertex(
            obj.Vertices[index],
            Vector3.UnitY,
            new Vector4(intensity, intensity, intensity, 1f),
            Vector2.Zero);
    }

    private static ModelVertex MakeDdmVertex(DdmVertex vertex, float normalOffset = 0f)
    {
        var normal = NormalizeOrDefault(new Vector3(-vertex.NX, -vertex.NY, vertex.NZ));
        var position = new Vector3(-vertex.X, -vertex.Y, vertex.Z);
        if (normalOffset > 0f)
            position += normal * normalOffset;

        return new ModelVertex(
            position,
            normal,
            new Vector4(vertex.R / 255f, vertex.G / 255f, vertex.B / 255f, vertex.A / 255f),
            new Vector2(vertex.U, vertex.V));
    }

    private static ModelVertex MakePsxVertex(
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        int slot,
        Vector4 color,
        (int Width, int Height) texDims)
    {
        var vertexIndex = GetPsxFaceVertexIndex(face, slot);
        if (vertexIndex >= mesh.Vertices.Count)
            return new ModelVertex(Vector3.Zero, Vector3.UnitY, color, Vector2.Zero);

        var nativeVertex = mesh.Vertices[(int)vertexIndex];
        var texCoord = face.GetTextureCoordinate(slot);
        return new ModelVertex(
            new Vector3(nativeVertex.X, -nativeVertex.Y, -nativeVertex.Z),
            ComputePsxVertexNormal(mesh, face, vertexIndex),
            color,
            ComputePsxTextureUv(version, face, texCoord.U, texCoord.V, texDims.Width, texDims.Height));
    }

    private static ModelVertex MakePs2Vertex(
        Ps2Vertex vertex,
        bool preserveVertexAlpha,
        bool bakeVertexColorsToWhite,
        Vector3? fallbackNormal = null)
    {
        var r = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.R / 128f, 1f);
        var g = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.G / 128f, 1f);
        var b = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.B / 128f, 1f);
        var a = bakeVertexColorsToWhite ? 1f : preserveVertexAlpha ? Math.Min(vertex.A / 128f, 1f) : 1f;

        // Worldzone leaves rarely carry per-vertex normals in their source VIF
        // batches — `vertex.HasNormal == false` is common — so callers may pass
        // a flat face normal as a fallback for optional lighting and viewer
        // normals. Source vertex colours do not depend on this fallback.
        var hasUsableNormal = vertex.HasNormal || fallbackNormal.HasValue;
        var normal = vertex.HasNormal
            ? NormalizeOrDefault(vertex.Normal)
            : fallbackNormal ?? Vector3.UnitY;

        if (!bakeVertexColorsToWhite && _activePs2WorldzoneLighting is { } lighting && hasUsableNormal)
        {
            var diffuse = MathF.Max(0f, Vector3.Dot(normal, lighting.SunDirection));
            var rf = lighting.Ambient.X + diffuse * lighting.SunColor.X;
            var gf = lighting.Ambient.Y + diffuse * lighting.SunColor.Y;
            var bf = lighting.Ambient.Z + diffuse * lighting.SunColor.Z;
            r = Math.Min(r * rf, 1f);
            g = Math.Min(g * gf, 1f);
            b = Math.Min(b * bf, 1f);
        }

        return new ModelVertex(
            vertex.Position,
            normal,
            new Vector4(r, g, b, a),
            vertex.HasUV ? new Vector2(vertex.U, 1f - vertex.V) : Vector2.Zero);
    }

    private static ModelVertex MakeXbxVertex(XbxVertex vertex, float coordinateScale)
    {
        var color = vertex.HasColor
            ? new Vector4(
                Math.Min(vertex.Color.X, 1f),
                Math.Min(vertex.Color.Y, 1f),
                Math.Min(vertex.Color.Z, 1f),
                Math.Min(vertex.Color.W, 1f))
            : Vector4.One;

        return new ModelVertex(
            vertex.Position * coordinateScale,
            vertex.HasNormal ? NormalizeOrDefault(vertex.Normal) : Vector3.UnitY,
            color,
            vertex.TexCoord);
    }

    private static ModelVertex MakeRwVertex(RwGeometry geometry, int index)
    {
        var position = index < geometry.Vertices.Length ? geometry.Vertices[index] : Vector3.Zero;
        var normal = geometry.Normals != null && index < geometry.Normals.Length
            ? NormalizeOrDefault(geometry.Normals[index])
            : Vector3.UnitY;
        var color = geometry.Colors != null && index < geometry.Colors.Length
            ? ToColor(geometry.Colors[index])
            : Vector4.One;
        var uv = geometry.UVs != null && index < geometry.UVs.Length ? geometry.UVs[index] : Vector2.Zero;
        return new ModelVertex(position, normal, color, uv);
    }

    private static ModelVertex MakeRwBspVertex(RwBspSection section, int index)
    {
        var position = index < section.Vertices.Length ? section.Vertices[index] : Vector3.Zero;
        var normal = section.Normals != null && index < section.Normals.Length
            ? NormalizeOrDefault(section.Normals[index])
            : Vector3.UnitY;
        var color = section.Colors != null && index < section.Colors.Length
            ? ToColor(section.Colors[index])
            : Vector4.One;
        var uv = section.UVs != null && index < section.UVs.Length ? section.UVs[index] : Vector2.Zero;
        return new ModelVertex(position, normal, color, uv);
    }

    private static void ApplyDdmMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        DdmMaterial material,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs)
    {
        renderMaterial.BaseColor = new Vector4(
            material.DiffuseR / 255f,
            material.DiffuseG / 255f,
            material.DiffuseB / 255f,
            material.DiffuseA / 255f);

        var isAdditive = material.BlendMode is 1 or 3;
        if (!material.TextureName.Equals("No_Texture_Map", StringComparison.OrdinalIgnoreCase))
        {
            var loaded = MeshTextureHelper.LoadTexture(textureDirs, material.TextureName, ddxTextures);
            if (loaded != null)
            {
                var pngBytes = isAdditive
                    ? MeshTextureHelper.ConvertLuminanceToAlpha(loaded.Value.Bytes)
                    : loaded.Value.Bytes;
                renderMaterial.TextureIndex ??= AddTexture(document, material.TextureName, pngBytes);
                renderMaterial.AlphaMode = isAdditive || loaded.Value.HasAlpha
                    ? ModelAlphaMode.Blend
                    : material.BlendMode == 2
                        ? ModelAlphaMode.Mask
                        : ModelAlphaMode.Opaque;
            }
        }

        if (isAdditive)
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        else if (material.BlendMode == 2)
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
    }

    private static void ApplyPs2Material(
        ModelDocument document,
        RenderMaterial renderMaterial,
        Ps2Material material,
        MeshChecksumTextureResolver? textureProvider)
    {
        if (textureProvider != null && material.TextureChecksum != 0)
        {
            var pngBytes = textureProvider(material.TextureChecksum);
            if (pngBytes != null)
            {
                renderMaterial.TextureIndex ??= AddTexture(
                    document,
                    ResolveQbName(material.TextureChecksum, $"tex_{material.TextureChecksum:X8}"),
                    pngBytes,
                    material.TextureChecksum,
                    material.ClampU ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat,
                    material.ClampV ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat);
            }
        }

        if ((material.Flags & (uint)Ps2MaterialFlags.Transparent) == 0)
        {
            if (material.AlphaRef >= 1)
            {
                renderMaterial.AlphaMode = ModelAlphaMode.Mask;
                renderMaterial.AlphaCutoff = material.AlphaRef / 255f;
            }

            return;
        }

        if (material.IsOpaqueBlend)
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            return;
        }

        var fixedOpacity = material.FixedBlendOpacity;
        if (fixedOpacity.HasValue && fixedOpacity.Value >= Ps2SceneRenderSemantics.FixBlendOpaqueThreshold / 128f)
            return;

        renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        if (fixedOpacity.HasValue)
            renderMaterial.BaseColor = new Vector4(1f, 1f, 1f, fixedOpacity.Value);
    }

    private static void ApplyPs2GeomMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        Ps2GeomLeaf leaf,
        MeshChecksumTextureResolver? textureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        Ps2TexaTextureResolver? texaTextureProvider = null,
        uint? textureChecksumOverride = null,
        bool useTextureAlphaMode = false,
        string? alphaModeOverride = null)
    {
        var textureChecksum = textureChecksumOverride
                              ?? (leaf.TextureChecksum != 0
                                  ? leaf.TextureChecksum
                                  : tex0Resolver?.Invoke(leaf.DmaTex0, leaf.GroupChecksum) ?? 0);
        byte[]? pngBytes = null;
        if ((textureProvider != null || texaTextureProvider != null) && textureChecksum != 0)
        {
            pngBytes = texaTextureProvider?.Invoke(textureChecksum, leaf.DmaTexa)
                       ?? textureProvider?.Invoke(textureChecksum);
            if (pngBytes != null)
            {
                renderMaterial.TextureIndex ??= AddTexture(
                    document,
                    ResolveQbName(textureChecksum, $"tex_{textureChecksum:X8}"),
                    pngBytes,
                    textureChecksum,
                    Ps2ClampToWrap((uint)(leaf.DmaClamp1 & 0x3)),
                    Ps2ClampToWrap((uint)((leaf.DmaClamp1 >> 2) & 0x3)));
            }
        }

        var alphaMode = alphaModeOverride ?? ClassifyPs2GeomEffectiveAlphaMode(leaf, pngBytes, useTextureAlphaMode);
        if (alphaMode == "MASK")
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            renderMaterial.AlphaCutoff = useTextureAlphaMode
                ? 0.5f
                : Ps2GeomRenderSemantics.ComputeAlphaMaskCutoff(leaf.DmaTest1);
            return;
        }

        if (alphaMode == "BLEND")
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
            ApplyPs2FixedBlendAlpha(renderMaterial, leaf.DmaAlpha1);
        }
    }

    private static string ClassifyPs2GeomEffectiveAlphaMode(
        Ps2GeomLeaf leaf,
        byte[]? pngBytes,
        bool useTextureAlphaMode)
    {
        if (useTextureAlphaMode && pngBytes != null)
        {
            return Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes);
        }

        if (Ps2GeomDestinationAlphaSynthesis.ShouldFallbackToSourceAlphaBlend(leaf))
            return "BLEND";

        var alphaMode = Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf);
        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        if (alphaMode == "BLEND" &&
            Ps2GeomRenderSemantics.IsStandardSourceAlphaBlend(alphaBlend) &&
            Ps2GeomSourceAlphaIsOpaque(leaf, pngBytes))
        {
            return Ps2GeomRenderSemantics.UsesAlphaTestMask(leaf.DmaTest1)
                ? "MASK"
                : "OPAQUE";
        }

        return alphaMode;
    }

    private static bool Ps2GeomSourceAlphaIsOpaque(Ps2GeomLeaf leaf, byte[]? pngBytes)
    {
        if (pngBytes == null ||
            Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes) != "OPAQUE")
        {
            return false;
        }

        return leaf.Vertices.All(static vertex => vertex.IsStripRestart || vertex.A >= 128);
    }

    private static void ApplyPs2FixedBlendAlpha(RenderMaterial renderMaterial, ulong alpha)
    {
        var alphaBlend = (byte)(alpha & 0xFF);
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        if (aField != 0 || bField != 1 || cField != 2 || dField != 1)
            return;

        var opacity = Math.Clamp(((alpha >> 32) & 0xFF) / 128f, 0f, 1f);
        renderMaterial.BaseColor = new Vector4(
            renderMaterial.BaseColor.X,
            renderMaterial.BaseColor.Y,
            renderMaterial.BaseColor.Z,
            opacity);
    }

    private static void ApplyXbxMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        XbxMaterial material,
        MeshChecksumTextureResolver? textureProvider)
    {
        var textureAlphaMode = "OPAQUE";
        if (textureProvider != null && material.Passes.Length > 0)
        {
            var pass = material.Passes[0];
            if (pass.TextureChecksum != 0)
            {
                var pngBytes = textureProvider(pass.TextureChecksum);
                if (pngBytes != null)
                {
                    textureAlphaMode = Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes);
                    renderMaterial.TextureIndex ??= AddTexture(
                        document,
                        ResolveQbName(pass.TextureChecksum, $"tex_{pass.TextureChecksum:X8}"),
                        pngBytes,
                        pass.TextureChecksum,
                        pass.UAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat,
                        pass.VAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat);
                }
            }
        }

        var firstBlendMode = material.Passes.Length > 0 ? material.Passes[0].BlendMode : 0;
        if (textureAlphaMode == "BLEND" && (firstBlendMode != 0 || material.Sorted))
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        }
        else if (textureAlphaMode == "MASK" ||
                 (material.AlphaCutoff >= 1 && textureAlphaMode != "OPAQUE"))
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            renderMaterial.AlphaCutoff = material.AlphaCutoff >= 1
                ? material.AlphaCutoff / 255f
                : 0.5f;
        }
        else if (textureAlphaMode == "BLEND")
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        }
    }

    private static int AddRwMaterial(
        ModelDocument document,
        RwMaterial material,
        MeshNamedTextureResolver? textureProvider,
        bool forBsp)
    {
        var renderMaterial = new RenderMaterial
        {
            Name = material.TextureName ?? $"mat_{material.R}_{material.G}_{material.B}_{material.A}"
        };
        renderMaterial.NativeMetadata.Add(new RwGsAlphaRenderMetadata(
            material.GsAlpha,
            material.GsAlphaFix,
            material.IsAdditive,
            material.IsSubtractive,
            material.IsBlend,
            material.TextureName));
        ApplyRwMaterial(document, renderMaterial, material, textureProvider, forBsp);
        return AddMaterial(document, renderMaterial);
    }

    private static void ApplyRwMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        RwMaterial material,
        MeshNamedTextureResolver? textureProvider,
        bool forBsp)
    {
        renderMaterial.BaseColor = new Vector4(
            material.R / 255f,
            material.G / 255f,
            material.B / 255f,
            material.A / 255f);

        var textureHasAlpha = false;
        if (textureProvider != null && !string.IsNullOrEmpty(material.TextureName))
        {
            var pngBytes = textureProvider(material.TextureName);
            if (pngBytes != null)
            {
                if (forBsp && material.IsAdditive)
                {
                    pngBytes = MeshTextureHelper.ConvertBlendTexture(pngBytes, 255, 255, 255);
                    textureHasAlpha = true;
                }
                else if (forBsp && material.IsSubtractive)
                {
                    pngBytes = MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0);
                    textureHasAlpha = true;
                }
                else if (forBsp)
                {
                    (pngBytes, textureHasAlpha) = MeshTextureHelper.ApplyColorKey(pngBytes);
                }

                renderMaterial.TextureIndex ??= AddTexture(document, material.TextureName, pngBytes);
            }
        }

        if (material.A < 255 || material.IsBlend)
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        else if (textureHasAlpha)
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
    }

    private static int GetOrCreatePsxMaterial(
        ModelDocument document,
        uint textureHash,
        bool semiTransparent,
        MeshChecksumTextureResolver? textureProvider,
        Dictionary<uint, (int Width, int Height)> textureDims,
        Dictionary<(uint Hash, bool SemiTransparent), int> materialCache)
    {
        var key = (textureHash, semiTransparent);
        if (materialCache.TryGetValue(key, out var existing))
            return existing;

        var name = ResolveQbName(textureHash, $"tex_{textureHash:X8}");
        if (semiTransparent)
            name += "__semitrans";

        var material = new RenderMaterial
        {
            Name = name,
            AlphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Opaque
        };

        if (textureProvider != null)
        {
            var pngBytes = textureProvider(textureHash);
            if (pngBytes != null)
            {
                var (processed, hasAlpha) = MeshTextureHelper.ApplyColorKey(pngBytes);
                if (semiTransparent)
                {
                    processed = MeshTextureHelper.ConvertLuminanceToAlpha(processed);
                    hasAlpha = true;
                }

                material.TextureIndex = AddTexture(document, name, processed, textureHash);
                if (hasAlpha)
                    material.AlphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Mask;
                if (TryExtractPngDimensions(processed) is { } dims)
                    textureDims[textureHash] = dims;
            }
        }

        var index = AddMaterial(document, material);
        materialCache[key] = index;
        return index;
    }

    private static (Vector4 C0, Vector4 C1, Vector4 C2, Vector4 C3) ComputePsxFaceColors(
        ushort version,
        PsxFace face,
        Vector4[]? gouraudPalette)
    {
        if (face.IsGouraud && gouraudPalette != null && version != 0x06)
        {
            var c0 = face.R < gouraudPalette.Length ? gouraudPalette[face.R] : Vector4.One;
            var c1 = face.G < gouraudPalette.Length ? gouraudPalette[face.G] : Vector4.One;
            var c2 = face.B < gouraudPalette.Length ? gouraudPalette[face.B] : Vector4.One;
            var c3 = face.IsQuad && face.Mode < gouraudPalette.Length ? gouraudPalette[face.Mode] : c0;
            return (c0, c1, c2, c3);
        }

        var flat = face.IsGouraud
            ? Vector4.One
            : new Vector4(
                Math.Min(face.R / 128f, 1f),
                Math.Min(face.G / 128f, 1f),
                Math.Min(face.B / 128f, 1f),
                1f);
        return (flat, flat, flat, flat);
    }

    private static Vector3 ComputePsxVertexNormal(PsxMesh mesh, PsxFace face, uint vertexIndex)
    {
        var normalIndex = mesh.HasPerVertexNormals && vertexIndex < mesh.VertexCount
            ? vertexIndex
            : face.NormalIndex;
        if (normalIndex >= mesh.Normals.Count)
            return Vector3.UnitY;

        var normal = mesh.Normals[(int)normalIndex];
        return NormalizeOrDefault(new Vector3(normal.X, -normal.Y, -normal.Z));
    }

    private static Vector2 ComputePsxTextureUv(
        ushort version,
        PsxFace face,
        int u,
        int v,
        int texWidth,
        int texHeight)
    {
        if (!face.IsTextured)
            return Vector2.Zero;

        return version == 0x06
            ? new Vector2(u / 512f, v / 512f)
            : new Vector2(u / (float)Math.Max(texWidth, 1), v / (float)Math.Max(texHeight, 1));
    }

    private static uint GetPsxFaceVertexIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private static bool UsesCombinedPsxCharacterAssembly(PsxMeshFile psxFile)
    {
        return psxFile.HasHierarchy ||
               psxFile.Meshes.Any(static mesh => mesh.Vertices.Any(static vertex =>
                   PsxMeshSemantics.IsExactStitchedReference(vertex.Type)));
    }

    private static HashSet<int> BuildPsxLodVariantSet(PsxMeshFile psxFile)
    {
        return psxFile.Meshes
            .Select(static mesh => (int)mesh.LodNextMeshIndex)
            .Where(index => index != ushort.MaxValue && index < psxFile.Meshes.Count)
            .ToHashSet();
    }

    private static string ResolvePsxMeshName(PsxMeshFile psxFile, int meshIndex)
    {
        var nameHash = meshIndex < psxFile.MeshNameHashes.Length ? psxFile.MeshNameHashes[meshIndex] : 0u;
        return ResolveQbName(nameHash, $"mesh_{meshIndex:X8}");
    }

    private static ModelPrimitive? AddPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        List<ModelVertex> vertices,
        List<int> indices)
    {
        if (indices.Count == 0)
            return null;

        var primitive = new ModelPrimitive
        {
            Name = name,
            MaterialIndex = materialIndex,
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray()
        };
        mesh.Primitives.Add(primitive);
        return primitive;
    }

    private static void AddTriangle(
        List<ModelVertex> vertices,
        List<int> indices,
        ModelVertex a,
        ModelVertex b,
        ModelVertex c)
    {
        if (IsDegenerate(a.Position, b.Position, c.Position))
            return;

        var offset = vertices.Count;
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        indices.Add(offset);
        indices.Add(offset + 1);
        indices.Add(offset + 2);
    }

    private static int? AddMesh(ModelDocument document, ModelMesh mesh)
    {
        if (mesh.Primitives.Count == 0)
            return null;

        var meshIndex = document.Meshes.Count;
        document.Meshes.Add(mesh);
        return meshIndex;
    }

    private static void AddMeshNode(
        ModelDocument document,
        string name,
        ModelMesh mesh,
        Matrix4x4? transform = null)
    {
        var meshIndex = AddMesh(document, mesh);
        if (!meshIndex.HasValue)
            return;

        AddMeshNode(document, name, meshIndex.Value, transform);
    }

    private static void AddMeshNode(
        ModelDocument document,
        string name,
        int meshIndex,
        Matrix4x4? transform = null)
    {
        if ((uint)meshIndex >= (uint)document.Meshes.Count)
            return;

        var nodeIndex = document.Nodes.Count;
        document.Nodes.Add(new ModelNode
        {
            Name = name,
            MeshIndex = meshIndex,
            Transform = transform ?? Matrix4x4.Identity
        });
        EnsureScene(document).RootNodeIndices.Add(nodeIndex);
    }

    private static ModelScene EnsureScene(ModelDocument document)
    {
        if (document.Scenes.Count == 0)
            document.Scenes.Add(new ModelScene { Name = document.Name });
        return document.Scenes[0];
    }

    private static int AddMaterial(ModelDocument document, RenderMaterial material)
    {
        document.Materials.Add(material);
        return document.Materials.Count - 1;
    }

    private static int AddTexture(
        ModelDocument document,
        string name,
        byte[] pngBytes,
        uint? checksum = null,
        ModelTextureWrap wrapU = ModelTextureWrap.Repeat,
        ModelTextureWrap wrapV = ModelTextureWrap.Repeat)
    {
        for (var i = 0; i < document.Textures.Count; i++)
        {
            var texture = document.Textures[i];
            if (checksum.HasValue && texture.NativeChecksum == checksum)
                return i;
            if (!checksum.HasValue && string.Equals(texture.Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        document.Textures.Add(new ModelTexture
        {
            Name = name,
            PngBytes = pngBytes,
            NativeChecksum = checksum,
            WrapU = wrapU,
            WrapV = wrapV
        });
        return document.Textures.Count - 1;
    }

    private static void FinalizeTriangleCount(ModelDocument document)
    {
        document.TriangleCount = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Sum(static primitive => primitive.TriangleCount);
    }

    private static Vector3 NormalizeOrDefault(Vector3 value)
    {
        var length = value.Length();
        return length > 0.001f && float.IsFinite(length) ? value / length : Vector3.UnitY;
    }

    private static bool IsDegenerate(Vector3 a, Vector3 b, Vector3 c)
    {
        const float epsilon = 1e-8f;
        if (Vector3.DistanceSquared(a, b) <= epsilon ||
            Vector3.DistanceSquared(b, c) <= epsilon ||
            Vector3.DistanceSquared(a, c) <= epsilon)
        {
            return true;
        }

        var cross = Vector3.Cross(b - a, c - a);
        return cross.LengthSquared() <= epsilon;
    }

    private static (Vector3, Vector3, Vector3) SortedTriangleKey(Vector3 a, Vector3 b, Vector3 c)
    {
        if (Compare(a, b) > 0) (a, b) = (b, a);
        if (Compare(b, c) > 0) (b, c) = (c, b);
        if (Compare(a, b) > 0) (a, b) = (b, a);
        return (a, b, c);

        static int Compare(Vector3 x, Vector3 y)
        {
            var cmp = x.X.CompareTo(y.X);
            if (cmp != 0) return cmp;
            cmp = x.Y.CompareTo(y.Y);
            return cmp != 0 ? cmp : x.Z.CompareTo(y.Z);
        }
    }

    private static Matrix4x4[] BuildRwFrameWorldTransforms(RwFrame[] frames)
    {
        var world = new Matrix4x4[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            var local = frames[i].LocalTransform;
            world[i] = frames[i].ParentIndex >= 0 && frames[i].ParentIndex < i
                ? local * world[frames[i].ParentIndex]
                : local;
        }

        return world;
    }

    private static ModelTextureWrap Ps2ClampToWrap(uint mode)
    {
        return mode is 1 or 2 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat;
    }

    private static Vector4 ToColor(RwVertexColor color)
    {
        return new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    private static bool IsRwDevTexture(string name)
    {
        return string.Equals(name, "wire", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "transparent", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_CR_Collision_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_CR_TriggerPlane_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_CR_VertPoly", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveQbName(uint checksum, string fallback)
    {
        return QbKey.QbKey.TryResolve(checksum) ?? fallback;
    }

    private static (int Width, int Height)? TryExtractPngDimensions(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.Length < 24)
            return null;

        var width = BinaryPrimitives.ReadInt32BigEndian(pngBytes[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(pngBytes[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private readonly record struct Ps2WorldzoneMaterialKey(
        uint TextureChecksum,
        ulong Clamp,
        ulong Alpha,
        ulong Test,
        ulong Texa,
        uint GroupChecksum,
        bool IsBillboard,
        string AlphaMode);
}
