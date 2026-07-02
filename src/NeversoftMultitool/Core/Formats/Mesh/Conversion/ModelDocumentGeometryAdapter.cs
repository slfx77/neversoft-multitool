using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
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
    private static Ps2WorldzoneLighting? _activePs2WorldzoneLighting;

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
        MeshChecksumTextureResolver? textureProvider,
        PshFile? pshFile = null,
        bool flatSkeleton = false,
        IReadOnlySet<int>? flatBoneIndices = null)
    {
        if (UsesCombinedPsxCharacterAssembly(psxFile))
        {
            PopulatePsxSkinned(
                document, psxFile, pshFile, textureProvider,
                flatSkeleton, flatBoneIndices);
            FinalizeTriangleCount(document);
            return;
        }

        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache = new Dictionary<(uint Hash, bool SemiTransparent), int>();
        var untexturedMaterial = AddMaterial(document, new RenderMaterial
        {
            Name = "untextured",
            BaseColor = new Vector4(0.7f, 0.7f, 0.7f, 1f)
        });

        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            var obj = psxFile.Objects[objectIndex];
            if (obj.MeshIndex >= psxFile.Meshes.Count)
                continue;

            var transform = Matrix4x4.CreateTranslation(
                PsxMeshSemantics.ToGltfPosition(PsxMeshSemantics.GetObjectOffset(psxFile, obj)));
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

        FinalizeTriangleCount(document);
    }

    public static void PopulatePs2Scene(
        ModelDocument document,
        ParsedPs2Scene scene,
        MeshChecksumTextureResolver? textureProvider,
        Ps2Skeleton? skeleton = null)
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

        int? skeletonIndex = null;
        if (skeleton != null)
        {
            skeletonIndex = document.Skeletons.Count;
            document.Skeletons.Add(BuildPs2Skeleton(skeleton));
        }

        var dedupByMaterial = new Dictionary<uint, HashSet<(Vector3, Vector3, Vector3)>>();
        // When the scene is skinned, fold every primitive into a single combined mesh
        // so the glTF exporter emits one skin shared across the whole character —
        // matching the legacy Ps2SceneGltfWriter behavior. Rigid scenes keep one
        // ModelMesh per native mesh group so per-group placement stays distinct.
        var skinnedMesh = skeletonIndex.HasValue ? new ModelMesh { Name = "skinned_mesh" } : null;
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

                var mesh = skinnedMesh ?? new ModelMesh { Name = groupName };
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
                    false,
                    skeletonIndex);
                if (skinnedMesh == null)
                    AddMeshNode(document, groupName, mesh);
            }
        }

        if (skinnedMesh != null)
            AddMeshNode(document, skinnedMesh.Name, skinnedMesh);

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
        _activePs2WorldzoneLighting = lighting;

        var typedEntries = PakArchive.GetTypedEntries(pakBytes);
        var mdlEntries = typedEntries
            .Where(static entry => entry.TypeHash is
                Ps2WorldzoneDetection.WorldzoneMdlTypeHash or
                Ps2WorldzoneDetection.WorldzoneLevelMdlTypeHash)
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
                mdlData = ExtendLevelMdlPreambleIfNeeded(pakBytes, mdlEntry, mdlData);
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

        var skinnedAtomic = clump.Atomics.FirstOrDefault(a => a.SkinData != null);
        if (skinnedAtomic?.SkinData != null)
        {
            PopulateRwDffSkinned(document, clump, skinnedAtomic.SkinData, materialMap);
            FinalizeTriangleCount(document);
            return;
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

    private static ModelPrimitive? AddPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        List<ModelVertex> vertices,
        List<int> indices,
        ModelSkinBinding? skin = null)
    {
        if (indices.Count == 0)
            return null;

        var primitive = new ModelPrimitive
        {
            Name = name,
            MaterialIndex = materialIndex,
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray(),
            Skin = skin
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

    private static void AddSkinnedTriangle(
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences> influences,
        ModelVertex va, ModelBoneInfluences ia,
        ModelVertex vb, ModelBoneInfluences ib,
        ModelVertex vc, ModelBoneInfluences ic)
    {
        if (IsDegenerate(va.Position, vb.Position, vc.Position))
            return;

        var offset = vertices.Count;
        vertices.Add(va);
        vertices.Add(vb);
        vertices.Add(vc);
        influences.Add(ia);
        influences.Add(ib);
        influences.Add(ic);
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
