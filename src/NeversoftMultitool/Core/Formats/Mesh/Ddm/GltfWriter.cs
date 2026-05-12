using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Lit;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Mesh.Ddm;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
///     Writes DDM mesh data to glTF 2.0 (.glb) files using SharpGLTF.
/// </summary>
public static class GltfWriter
{
    /// <summary>
    ///     Small offset applied along vertex normals for non-opaque geometry (decals, graffiti,
    ///     signs) to prevent z-fighting with coplanar walls in glTF viewers that lack depth bias.
    /// </summary>
    private const float DecalNormalOffset = 0.1f;

    /// <summary>
    ///     Writes a parsed DDM file to a .glb file without PSX placement.
    ///     Objects are placed at their local vertex positions (no world transforms).
    ///     For level assembly with world placement, use WritePlacedLevel instead.
    /// </summary>
    /// <param name="ddm">Parsed DDM data.</param>
    /// <param name="outputPath">Output .glb file path.</param>
    /// <param name="texturePath">Optional directory containing extracted DDX textures (subdirectories searched by DDM name).</param>
    /// <param name="ddmName">DDM filename stem, used to find matching texture subdirectory.</param>
    /// <param name="ddxTextures">Optional in-memory DDX texture cache (name → DDS bytes).</param>
    /// <param name="lights">Optional parsed .lit lights to embed in the glTF scene.</param>
    /// <returns>Total number of triangles written.</returns>
    public static int WriteDdm(DdmFile ddm, string outputPath, string? texturePath = null,
        string? ddmName = null, Dictionary<string, byte[]>? ddxTextures = null,
        List<LitLight>? lights = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var (model, triangles) = BuildDdmModel(ddm, texturePath, ddmName, ddxTextures, lights);
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    internal static (ModelRoot Model, int Triangles) BuildDdmModel(
        DdmFile ddm, string? texturePath = null, string? ddmName = null,
        Dictionary<string, byte[]>? ddxTextures = null, List<LitLight>? lights = null)
    {
        var textureDirs = MeshTextureHelper.BuildTextureSearchPaths(texturePath, ddmName);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<string, MaterialBuilder>();
        var totalTriangles = 0;

        foreach (var obj in ddm.Objects)
        {
            if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
                continue;

            var mesh = BuildDdmMesh(obj, textureDirs, materialCache, ddxTextures, out var tris);
            totalTriangles += tris;

            var node = new NodeBuilder(obj.Name);
            scene.AddRigidMesh(mesh, node);
        }

        if (lights != null)
            GltfLightWriter.AddLightsToScene(scene, lights);

        return (scene.ToGltf2(), totalTriangles);
    }

    /// <summary>
    ///     Writes a placed level: combines level DDM + objects DDM with PSX layout placement.
    ///     Produces up to three output files:
    ///     {name}_level.glb  — placed level geometry
    ///     {name}_objects.glb — placed objects
    ///     {name}.glb         — combined (all in one)
    ///     Coordinate mapping confirmed via THPS2X Xbox decompilation:
    ///     world_pos = psx_raw_int32 / 4096.0 (direct, no axis negation)
    ///     glTF output uses (-X, -Y, +Z) conversion matching MakeVertex.
    /// </summary>
    public static (int LevelTriangles, int ObjectTriangles) WritePlacedLevel(
        string levelDdmPath, string levelPsxPath,
        string? objectsDdmPath, string? objectsPsxPath,
        string outputDir, string levelName,
        string? ddxPath = null)
    {
        Directory.CreateDirectory(outputDir);

        var levelDdm = DdmFile.Parse(levelDdmPath);
        var levelPsx = PsxLayoutFile.Parse(levelPsxPath);

        // Load DDX textures for both level and objects
        var ddxTextures = MeshTextureHelper.LoadDdxTextures(ddxPath, levelName);

        // Load .lit lights
        var lights = GltfLightWriter.FindAndParseLitFile(levelName, ddxPath)
                     ?? GltfLightWriter.FindAndParseLitFile(levelName, Path.GetDirectoryName(levelDdmPath));

        var textureDirs = MeshTextureHelper.BuildTextureSearchPaths(null, null);

        // Level geometry
        var levelScene = new SceneBuilder();
        var levelMats = new Dictionary<string, MaterialBuilder>();
        var levelTriangles = AddDdmToScene(levelScene, levelDdm, levelPsx, textureDirs, levelMats, ddxTextures);
        if (lights != null) GltfLightWriter.AddLightsToScene(levelScene, lights);
        var levelModel = levelScene.ToGltf2();
        GltfNormalSmoother.SmoothNormals(levelModel);
        levelModel.SaveGLB(Path.Combine(outputDir, levelName + "_level.glb"));

        // Objects
        var objectTriangles = 0;
        DdmFile? objectsDdm = null;
        PsxLayoutFile? objectsPsx = null;
        if (objectsDdmPath != null && File.Exists(objectsDdmPath))
        {
            objectsDdm = DdmFile.Parse(objectsDdmPath);
            objectsPsx = objectsPsxPath != null ? PsxLayoutFile.Parse(objectsPsxPath) : null;

            var objScene = new SceneBuilder();
            var objMats = new Dictionary<string, MaterialBuilder>();
            objectTriangles = AddDdmToScene(objScene, objectsDdm, objectsPsx, textureDirs, objMats, ddxTextures);
            var objModel = objScene.ToGltf2();
            GltfNormalSmoother.SmoothNormals(objModel);
            objModel.SaveGLB(Path.Combine(outputDir, levelName + "_objects.glb"));
        }

        // Combined
        var combinedScene = new SceneBuilder();
        var combinedMats = new Dictionary<string, MaterialBuilder>();
        AddDdmToScene(combinedScene, levelDdm, levelPsx, textureDirs, combinedMats, ddxTextures);
        if (objectsDdm != null)
            AddDdmToScene(combinedScene, objectsDdm, objectsPsx, textureDirs, combinedMats, ddxTextures);
        if (lights != null) GltfLightWriter.AddLightsToScene(combinedScene, lights);
        var combinedModel = combinedScene.ToGltf2();
        GltfNormalSmoother.SmoothNormals(combinedModel);
        combinedModel.SaveGLB(Path.Combine(outputDir, levelName + ".glb"));

        return (levelTriangles, objectTriangles);
    }

    /// <summary>
    ///     Adds a DDM file to a scene, optionally placing objects via PSX layout.
    ///     Returns the number of triangles added.
    /// </summary>
    private static int AddDdmToScene(
        SceneBuilder scene, DdmFile ddm, PsxLayoutFile? psx,
        List<string> textureDirs, Dictionary<string, MaterialBuilder> materialCache,
        Dictionary<string, byte[]>? ddxTextures)
    {
        if (psx != null)
        {
            var (tris, placedIndices) = AddPlacedObjects(
                scene, ddm, psx, textureDirs, materialCache, ddxTextures);
            AddUnplacedObjects(scene, ddm, placedIndices, textureDirs, materialCache, ddxTextures);
            return tris;
        }

        AddUnplacedObjects(scene, ddm, new HashSet<int>(), textureDirs, materialCache, ddxTextures);
        return 0;
    }

    /// <summary>
    ///     Places DDM objects using PSX layout positions.
    ///     Position mapping: PSX raw int32 / 4096 → float, then (-X, -Y, +Z) for glTF.
    /// </summary>
    private static (int Triangles, HashSet<int> PlacedIndices) AddPlacedObjects(
        SceneBuilder scene, DdmFile ddm, PsxLayoutFile psxFile,
        List<string> textureDirs, Dictionary<string, MaterialBuilder> materialCache,
        Dictionary<string, byte[]>? ddxTextures)
    {
        var ddmByHash = DdmHashLookup.Build(ddm);
        var meshSlotToDdm = DdmHashLookup.ResolveMeshIndices(psxFile, ddmByHash);
        var placedIndices = new HashSet<int>();
        var meshCache = new Dictionary<int, MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>>();
        var totalTriangles = 0;

        foreach (var psxObj in psxFile.Objects)
        {
            if (!meshSlotToDdm.TryGetValue(psxObj.MeshIndex, out var ddmIndex))
                continue;

            placedIndices.Add(ddmIndex);
            var obj = ddm.Objects[ddmIndex];

            if (!meshCache.TryGetValue(ddmIndex, out var mesh))
            {
                if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
                    continue;
                mesh = BuildDdmMesh(obj, textureDirs, materialCache, ddxTextures, out var tris);
                totalTriangles += tris;
                meshCache[ddmIndex] = mesh;
            }

            // Coordinate mapping from Xbox decompilation (FUN_0018b940, line 17795-17797):
            // world_pos = psx_raw_int32 * (1/4096)  — direct, no negation
            // glTF conversion matches MakeVertex: (-X, -Y, +Z)
            var translation = new Vector3(-psxObj.X, -psxObj.Y, psxObj.Z);
            scene.AddRigidMesh(mesh, Matrix4x4.CreateTranslation(translation));
        }

        return (totalTriangles, placedIndices);
    }

    /// <summary>
    ///     Adds DDM objects that were not matched by PSX placement (fallback at local origin).
    /// </summary>
    private static void AddUnplacedObjects(
        SceneBuilder scene, DdmFile ddm, HashSet<int> placedIndices,
        List<string> textureDirs, Dictionary<string, MaterialBuilder> materialCache,
        Dictionary<string, byte[]>? ddxTextures)
    {
        for (var i = 0; i < ddm.Objects.Count; i++)
        {
            if (placedIndices.Contains(i))
                continue;

            var obj = ddm.Objects[i];
            if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
                continue;

            var mesh = BuildDdmMesh(obj, textureDirs, materialCache, ddxTextures, out _);
            var node = new NodeBuilder(obj.Name);
            scene.AddRigidMesh(mesh, node);
        }
    }

    private static MaterialBuilder GetOrCreateMaterial(
        DdmMaterial mat, List<string> textureDirs,
        Dictionary<string, MaterialBuilder> cache,
        Dictionary<string, byte[]>? ddxTextures)
    {
        // Additive materials need luminance-to-alpha conversion, so cache separately
        var isAdditive = mat.BlendMode is 1 or 3;
        var key = isAdditive ? $"{mat.TextureName}__additive" : mat.TextureName;
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var builder = new MaterialBuilder(mat.Name)
            .WithDoubleSide(true)
            .WithUnlitShader();

        // White base color — vertex colors and textures provide all color/lighting
        builder.WithBaseColor(new Vector4(1, 1, 1, 1));

        var hasTextureAlpha = false;

        // Try to find and load a texture
        if ((textureDirs.Count > 0 || ddxTextures?.Count > 0) &&
            !mat.TextureName.Equals("No_Texture_Map", StringComparison.OrdinalIgnoreCase))
        {
            var loaded = MeshTextureHelper.LoadTexture(textureDirs, mat.TextureName, ddxTextures);
            if (loaded != null)
            {
                var pngBytes = loaded.Value.Bytes;
                hasTextureAlpha = loaded.Value.HasAlpha;

                // For additive blend modes (light cones, flares, glows), convert
                // texture luminance to alpha. In-game, additive blending makes dark
                // pixels invisible and bright pixels add light. In glTF (no additive
                // mode), we approximate by making dark pixels transparent and setting
                // bright pixels to white with proportional alpha.
                // Always apply for additive modes — even DXT3/DXT5 textures with an
                // alpha channel need conversion, since additive blending ignores alpha
                // and uses luminance only. Existing alpha is used as a multiplier.
                if (isAdditive)
                {
                    pngBytes = MeshTextureHelper.ConvertLuminanceToAlpha(pngBytes);
                    hasTextureAlpha = true;
                }

                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);
            }
        }

        // Set alpha mode based on BlendMode and actual texture alpha
        // BlendMode 1,3 = additive/glow (BLEND is closest glTF approximation)
        // BlendMode 2   = alpha test/cutout (MASK preserves Z-order for fences, leaves, etc.)
        // BlendMode 0   = opaque unless texture has transparent pixels
        if (isAdditive || hasTextureAlpha)
            builder.WithAlpha(AlphaMode.BLEND);
        else if (mat.BlendMode == 2)
            builder.WithAlpha(AlphaMode.MASK);

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    ///     Converts a triangle strip segment to individual triangles and adds them to the primitive.
    /// </summary>
    private static int AddTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        DdmObject obj,
        DdmSplit split,
        float normalOffset = 0f)
    {
        var triangleCount = 0;
        var end = split.IndexOffset + split.IndexCount;

        for (var i = split.IndexOffset; i + 2 < end; i++)
        {
            var ai = obj.Indices[i];
            var bi = obj.Indices[i + 1];
            var ci = obj.Indices[i + 2];

            // Skip degenerate triangles (strip restart markers)
            if (ai == bi || ai == ci || bi == ci)
                continue;

            var va = MakeVertex(obj.Vertices[ai], normalOffset);
            var vb = MakeVertex(obj.Vertices[bi], normalOffset);
            var vc = MakeVertex(obj.Vertices[ci], normalOffset);

            // Flip winding on odd triangles to maintain consistent face orientation
            var stripIndex = i - split.IndexOffset;
            if (stripIndex % 2 == 0)
                prim.AddTriangle(va, vb, vc);
            else
                prim.AddTriangle(vb, va, vc);

            triangleCount++;
        }

        return triangleCount;
    }

    private static VERTEX MakeVertex(DdmVertex v, float normalOffset = 0f)
    {
        var pos = new Vector3(-v.X, -v.Y, v.Z);
        var normal = new Vector3(-v.NX, -v.NY, v.NZ);

        if (normalOffset > 0f)
            pos += normal * normalOffset;

        return new VERTEX(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(
                new Vector4(v.R / 255f, v.G / 255f, v.B / 255f, v.A / 255f),
                new Vector2(v.U, v.V)));
    }

    /// <summary>
    ///     Builds a glTF mesh from a DDM object's geometry in local space.
    /// </summary>
    private static MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> BuildDdmMesh(
        DdmObject obj,
        List<string> textureDirs,
        Dictionary<string, MaterialBuilder> materialCache,
        Dictionary<string, byte[]>? ddxTextures,
        out int triangleCount)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(obj.Name);
        triangleCount = 0;

        // Detect flat/planar objects (separate decal objects sitting on other geometry).
        var minExtent = Math.Min(obj.BBoxExtentX, Math.Min(obj.BBoxExtentY, obj.BBoxExtentZ));
        var isFlat = minExtent < 1.5f;

        // Build drawOrder → rank mapping so overlay materials get pushed outward.
        // Use rank (not raw magnitude) to keep offsets small and consistent.
        var drawOrderRanks = BuildDrawOrderRanks(obj);

        for (var splitIndex = 0; splitIndex < obj.Splits.Count; splitIndex++)
        {
            var split = obj.Splits[splitIndex];
            if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                continue;

            var mat = obj.Materials[split.MaterialIndex];
            var material = GetOrCreateMaterial(mat, textureDirs, materialCache, ddxTextures);
            var prim = mesh.UsePrimitive(material);

            // Prevent z-fighting for coplanar geometry in glTF viewers lacking depth bias.
            // Higher drawOrder rank = overlay (graffiti, decals) pushed outward along normals.
            var rank = drawOrderRanks.GetValueOrDefault(mat.DrawOrder);
            var drawOrderOffset = rank * DecalNormalOffset;
            var materialOffset = isFlat || mat.BlendMode != 0 ? DecalNormalOffset : 0f;
            var offset = Math.Max(drawOrderOffset, materialOffset);
            triangleCount += AddTriangleStrip(prim, obj, split, offset);
        }

        return mesh;
    }

    private static Dictionary<uint, int> BuildDrawOrderRanks(DdmObject obj)
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
}
