using System.Numerics;
using NeversoftMultitool.Core.Formats.Psx;
using Pfim;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Formats.Mesh;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
/// Writes DDM mesh data to glTF 2.0 (.glb) files using SharpGLTF.
/// </summary>
public static class GltfWriter
{
    /// <summary>
    /// Writes a parsed DDM file to a .glb file.
    /// </summary>
    /// <param name="ddm">Parsed DDM data.</param>
    /// <param name="outputPath">Output .glb file path.</param>
    /// <param name="texturePath">Optional directory containing extracted DDX textures (subdirectories searched by DDM name).</param>
    /// <param name="ddmName">DDM filename stem, used to find matching texture subdirectory.</param>
    /// <returns>Total number of triangles written.</returns>
    public static int WriteDdm(DdmFile ddm, string outputPath, string? texturePath = null, string? ddmName = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Build list of texture search directories
        var textureDirs = BuildTextureSearchPaths(texturePath, ddmName);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<string, MaterialBuilder>();
        var totalTriangles = 0;

        foreach (var obj in ddm.Objects)
        {
            if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
                continue;

            var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(obj.Name);

            foreach (var split in obj.Splits)
            {
                if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                    continue;

                var mat = obj.Materials[split.MaterialIndex];
                var material = GetOrCreateMaterial(mat, textureDirs, materialCache);
                var prim = mesh.UsePrimitive(material);

                totalTriangles += AddTriangleStrip(prim, obj, split);
            }

            var node = new NodeBuilder(obj.Name);
            scene.AddRigidMesh(mesh, node);
        }

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
    }

    private static List<string> BuildTextureSearchPaths(string? texturePath, string? ddmName)
    {
        var dirs = new List<string>();
        if (string.IsNullOrEmpty(texturePath))
            return dirs;

        // DDX extraction creates subdirectories matching DDM name (e.g. textures/skware/)
        if (!string.IsNullOrEmpty(ddmName))
        {
            // DDX archive extraction nests: DDX/<name>/<name>/*.DDS
            var nestedDir = Path.Combine(texturePath, ddmName, ddmName);
            if (Directory.Exists(nestedDir))
                dirs.Add(nestedDir);

            var subDir = Path.Combine(texturePath, ddmName);
            if (Directory.Exists(subDir))
                dirs.Add(subDir);
        }

        // Also search the root texture directory
        if (Directory.Exists(texturePath))
            dirs.Add(texturePath);

        return dirs;
    }

    private static MaterialBuilder GetOrCreateMaterial(
        DdmMaterial mat, List<string> textureDirs,
        Dictionary<string, MaterialBuilder> cache)
    {
        var key = mat.TextureName;
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var builder = new MaterialBuilder(mat.Name)
            .WithDoubleSide(true)
            .WithUnlitShader();

        // White base color — vertex colors and textures provide all color/lighting
        builder.WithBaseColor(new Vector4(1, 1, 1, 1));

        // Set alpha mode for transparent materials
        if (mat.BlendMode > 0)
            builder.WithAlpha(AlphaMode.BLEND);

        // Try to find and load a texture
        if (textureDirs.Count > 0 &&
            !mat.TextureName.Equals("No_Texture_Map", StringComparison.OrdinalIgnoreCase))
        {
            var imageBytes = LoadTexture(textureDirs, mat.TextureName);
            if (imageBytes != null)
            {
                var memImage = new MemoryImage(imageBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);
            }
        }

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    /// Searches texture directories for a matching texture file (PNG or DDS).
    /// DDS files are decoded and converted to PNG bytes in memory.
    /// </summary>
    private static byte[]? LoadTexture(List<string> textureDirs, string textureName)
    {
        foreach (var dir in textureDirs)
        {
            // Try PNG first (native glTF format)
            var pngFiles = Directory.GetFiles(dir, textureName + ".png",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            if (pngFiles.Length > 0)
                return File.ReadAllBytes(pngFiles[0]);

            // Try DDS (decode to PNG)
            var ddsFiles = Directory.GetFiles(dir, textureName + ".dds",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            if (ddsFiles.Length > 0)
                return DecodeDdsToPng(ddsFiles[0]);
        }

        return null;
    }

    /// <summary>
    /// Decodes a DDS file to PNG bytes using Pfim for DXT decompression
    /// and ImageSharp for PNG encoding.
    /// </summary>
    private static byte[]? DecodeDdsToPng(string ddsPath)
    {
        try
        {
            using var image = Pfimage.FromFile(ddsPath);

            // Pfim decodes to Rgba32 or Rgb24
            using var ms = new MemoryStream();

            if (image.Format == ImageFormat.Rgba32)
            {
                using var img = Image.LoadPixelData<Bgra32>(image.Data, image.Width, image.Height);
                img.SaveAsPng(ms);
            }
            else if (image.Format == ImageFormat.Rgb24)
            {
                using var img = Image.LoadPixelData<Bgr24>(image.Data, image.Width, image.Height);
                img.SaveAsPng(ms);
            }
            else
            {
                return null;
            }

            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts a triangle strip segment to individual triangles and adds them to the primitive.
    /// </summary>
    private static int AddTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        DdmObject obj,
        DdmSplit split)
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

            var va = MakeVertex(obj.Vertices[ai]);
            var vb = MakeVertex(obj.Vertices[bi]);
            var vc = MakeVertex(obj.Vertices[ci]);

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

    /// <summary>
    /// Makes a vertex with X/Y negation for non-placed (local-space) export.
    /// </summary>
    private static VERTEX MakeVertex(DdmVertex v)
    {
        return new VERTEX(
            new VertexPositionNormal(
                new Vector3(-v.X, -v.Y, v.Z),
                new Vector3(-v.NX, -v.NY, v.NZ)),
            new VertexColor1Texture1(
                new Vector4(v.R / 255f, v.G / 255f, v.B / 255f, v.A / 255f),
                new Vector2(v.U, v.V)));
    }

    private const float WorldScale = 2.833f;

    /// <summary>
    /// Makes a vertex with world-space transform applied using PSX position data.
    /// Formula: world = -(raw + offset) / 2.833
    /// </summary>
    private static VERTEX MakePlacedVertex(DdmVertex v, PsxObjectPosition pos)
    {
        return new VERTEX(
            new VertexPositionNormal(
                new Vector3(
                    -(v.X + pos.X) / WorldScale,
                    -(v.Y + pos.Y) / WorldScale,
                    -(v.Z + pos.Z) / WorldScale),
                new Vector3(-v.NX, -v.NY, -v.NZ)),
            new VertexColor1Texture1(
                new Vector4(v.R / 255f, v.G / 255f, v.B / 255f, v.A / 255f),
                new Vector2(v.U, v.V)));
    }

    /// <summary>
    /// Converts a triangle strip using placed (world-space) vertices.
    /// </summary>
    private static int AddPlacedTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        DdmObject obj,
        DdmSplit split,
        PsxObjectPosition pos)
    {
        var triangleCount = 0;
        var end = split.IndexOffset + split.IndexCount;

        for (var i = split.IndexOffset; i + 2 < end; i++)
        {
            var ai = obj.Indices[i];
            var bi = obj.Indices[i + 1];
            var ci = obj.Indices[i + 2];

            if (ai == bi || ai == ci || bi == ci)
                continue;

            var va = MakePlacedVertex(obj.Vertices[ai], pos);
            var vb = MakePlacedVertex(obj.Vertices[bi], pos);
            var vc = MakePlacedVertex(obj.Vertices[ci], pos);

            var stripIndex = i - split.IndexOffset;
            if (stripIndex % 2 == 0)
                prim.AddTriangle(va, vb, vc);
            else
                prim.AddTriangle(vb, va, vc);

            triangleCount++;
        }

        return triangleCount;
    }

    /// <summary>
    /// Adds placed DDM objects to an existing scene using PSX world positions.
    /// </summary>
    private static int AddPlacedObjects(
        SceneBuilder scene,
        DdmFile ddm,
        List<PsxObjectPosition> positions,
        List<string> textureDirs,
        Dictionary<string, MaterialBuilder> materialCache)
    {
        // Build index lookup: mesh index -> PSX position
        var positionByIndex = new Dictionary<ushort, PsxObjectPosition>();
        foreach (var pos in positions)
        {
            positionByIndex.TryAdd(pos.MeshIndex, pos);
        }

        var totalTriangles = 0;

        for (var objIdx = 0; objIdx < ddm.Objects.Count; objIdx++)
        {
            var obj = ddm.Objects[objIdx];
            if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
                continue;

            if (!positionByIndex.TryGetValue((ushort)objIdx, out var pos))
                continue;

            var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(obj.Name);

            foreach (var split in obj.Splits)
            {
                if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                    continue;

                var mat = obj.Materials[split.MaterialIndex];
                var material = GetOrCreateMaterial(mat, textureDirs, materialCache);
                var prim = mesh.UsePrimitive(material);

                totalTriangles += AddPlacedTriangleStrip(prim, obj, split, pos);
            }

            var node = new NodeBuilder(obj.Name);
            scene.AddRigidMesh(mesh, node);
        }

        return totalTriangles;
    }

    /// <summary>
    /// Writes a placed level with up to three output files.
    /// </summary>
    /// <param name="levelDdm">Main level DDM (static geometry).</param>
    /// <param name="objectsDdm">Optional objects DDM (_o file, interactive objects).</param>
    /// <param name="positions">PSX object positions for world placement.</param>
    /// <param name="outputDir">Output directory.</param>
    /// <param name="levelName">Level stem name (e.g. "skware").</param>
    /// <param name="texturePath">Optional texture directory.</param>
    /// <returns>(combined triangles, level triangles, objects triangles)</returns>
    public static (int Combined, int Level, int Objects) WritePlacedLevel(
        DdmFile levelDdm,
        DdmFile? objectsDdm,
        List<PsxObjectPosition> positions,
        string outputDir,
        string levelName,
        string? texturePath = null)
    {
        Directory.CreateDirectory(outputDir);
        var textureDirs = BuildTextureSearchPaths(texturePath, levelName);
        var materialCache = new Dictionary<string, MaterialBuilder>();

        // 1. Level-only (placed static geometry)
        var levelScene = new SceneBuilder();
        var levelTriangles = AddPlacedObjects(levelScene, levelDdm, positions, textureDirs, materialCache);
        var levelPath = Path.Combine(outputDir, levelName + "_level.glb");
        levelScene.ToGltf2().SaveGLB(levelPath);

        // 2. Objects-only (local space, non-placed)
        var objectsTriangles = 0;
        if (objectsDdm != null)
        {
            var objectsPath = Path.Combine(outputDir, levelName + "_objects.glb");
            objectsTriangles = WriteDdm(objectsDdm, objectsPath, texturePath, levelName + "_o");
        }

        // 3. Combined (placed level + unplaced objects in one file)
        var combinedScene = new SceneBuilder();
        var combinedMaterialCache = new Dictionary<string, MaterialBuilder>();
        var combinedTriangles = AddPlacedObjects(combinedScene, levelDdm, positions, textureDirs, combinedMaterialCache);

        if (objectsDdm != null)
            combinedTriangles += AddUnplacedObjects(combinedScene, objectsDdm, textureDirs, combinedMaterialCache);

        var combinedPath = Path.Combine(outputDir, levelName + ".glb");
        combinedScene.ToGltf2().SaveGLB(combinedPath);

        return (combinedTriangles, levelTriangles, objectsTriangles);
    }

    /// <summary>
    /// Adds DDM objects to a scene in local space (with X/Y negation, no world placement).
    /// </summary>
    private static int AddUnplacedObjects(
        SceneBuilder scene,
        DdmFile ddm,
        List<string> textureDirs,
        Dictionary<string, MaterialBuilder> materialCache)
    {
        var totalTriangles = 0;

        foreach (var obj in ddm.Objects)
        {
            if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
                continue;

            var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(obj.Name);
            foreach (var split in obj.Splits)
            {
                if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                    continue;

                var mat = obj.Materials[split.MaterialIndex];
                var material = GetOrCreateMaterial(mat, textureDirs, materialCache);
                var prim = mesh.UsePrimitive(material);
                totalTriangles += AddTriangleStrip(prim, obj, split);
            }

            var node = new NodeBuilder(obj.Name);
            scene.AddRigidMesh(mesh, node);
        }

        return totalTriangles;
    }
}
