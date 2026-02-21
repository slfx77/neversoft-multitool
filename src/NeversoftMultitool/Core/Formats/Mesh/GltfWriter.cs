using System.Numerics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Archives;
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
    /// <param name="ddxTextures">Optional in-memory DDX texture cache (name → DDS bytes).</param>
    /// <returns>Total number of triangles written.</returns>
    public static int WriteDdm(DdmFile ddm, string outputPath, string? texturePath = null,
        string? ddmName = null, Dictionary<string, byte[]>? ddxTextures = null)
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

            var mesh = BuildDdmMesh(obj, textureDirs, materialCache, ddxTextures, out var tris);
            totalTriangles += tris;

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
            var loaded = LoadTexture(textureDirs, mat.TextureName, ddxTextures);
            if (loaded != null)
            {
                byte[] pngBytes = loaded.Value.Bytes;
                hasTextureAlpha = loaded.Value.HasAlpha;

                // For additive blend modes (light cones, flares, glows), convert
                // texture luminance to alpha. In-game, additive blending makes dark
                // pixels invisible and bright pixels add light. In glTF (no additive
                // mode), we approximate by making dark pixels transparent and setting
                // bright pixels to white with proportional alpha.
                if (isAdditive && !hasTextureAlpha)
                {
                    pngBytes = ConvertLuminanceToAlpha(pngBytes);
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
            builder.WithAlpha(AlphaMode.MASK, 0.5f);

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    /// Converts a texture for additive blend approximation in glTF.
    /// Converts texture to white RGB with luminance-derived alpha.
    /// Dark pixels become transparent (invisible, like additive black)
    /// and bright pixels become white with proportional opacity.
    /// </summary>
    private static byte[] ConvertLuminanceToAlpha(byte[] pngBytes)
    {
        using var img = Image.Load<Rgba32>(pngBytes);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    var lum = (byte)((pixel.R * 77 + pixel.G * 150 + pixel.B * 29) >> 8);
                    pixel = new Rgba32(255, 255, 255, lum);
                }
            }
        });

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Searches DDX cache and texture directories for a matching texture.
    /// DDS files are decoded and converted to PNG bytes in memory.
    /// </summary>
    private static (byte[] Bytes, bool HasAlpha)? LoadTexture(
        List<string> textureDirs, string textureName,
        Dictionary<string, byte[]>? ddxTextures)
    {
        // Check DDX in-memory cache first
        if (ddxTextures != null && ddxTextures.TryGetValue(textureName, out var ddsBytes))
        {
            using var ms = new MemoryStream(ddsBytes);
            return DecodeDdsToPng(ms);
        }

        // Search directories
        foreach (var dir in textureDirs)
        {
            // Try PNG first (native glTF format)
            var pngFiles = Directory.GetFiles(dir, textureName + ".png",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            if (pngFiles.Length > 0)
                return (File.ReadAllBytes(pngFiles[0]), false);

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
    private static (byte[] Bytes, bool HasAlpha)? DecodeDdsToPng(string ddsPath)
    {
        try
        {
            using var image = Pfimage.FromFile(ddsPath);
            return EncodePfimToPng(image);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes DDS bytes from a stream to PNG bytes.
    /// </summary>
    private static (byte[] Bytes, bool HasAlpha)? DecodeDdsToPng(Stream ddsStream)
    {
        try
        {
            using var image = Pfimage.FromStream(ddsStream);
            return EncodePfimToPng(image);
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] Bytes, bool HasAlpha)? EncodePfimToPng(IImage image)
    {
        using var ms = new MemoryStream();

        if (image.Format == ImageFormat.Rgba32)
        {
            // Check if any pixel actually has non-opaque alpha (BGRA layout: alpha at offset 3)
            var hasAlpha = false;
            var data = image.Data;
            var stride = image.Stride;
            for (var y = 0; y < image.Height && !hasAlpha; y++)
            {
                var rowStart = y * stride;
                for (var x = 0; x < image.Width; x++)
                {
                    if (data[rowStart + x * 4 + 3] < 255)
                    {
                        hasAlpha = true;
                        break;
                    }
                }
            }

            using var img = Image.LoadPixelData<Bgra32>(image.Data, image.Width, image.Height);
            img.SaveAsPng(ms);
            return (ms.ToArray(), hasAlpha);
        }

        if (image.Format == ImageFormat.Rgb24)
        {
            using var img = Image.LoadPixelData<Bgr24>(image.Data, image.Width, image.Height);
            img.SaveAsPng(ms);
            return (ms.ToArray(), false);
        }

        return null;
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

    /// <summary>
    /// Builds a glTF mesh from a DDM object's geometry in local space.
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

        foreach (var split in obj.Splits)
        {
            if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                continue;

            var mat = obj.Materials[split.MaterialIndex];
            var material = GetOrCreateMaterial(mat, textureDirs, materialCache, ddxTextures);
            var prim = mesh.UsePrimitive(material);
            triangleCount += AddTriangleStrip(prim, obj, split);
        }

        return mesh;
    }

    /// <summary>
    /// Adds placed DDM objects to a scene using PSX mesh name hashes for matching
    /// and glTF node transforms for world-space placement.
    /// Returns total triangles and the set of DDM object indices that were placed.
    /// </summary>
    private static (int Triangles, HashSet<int> PlacedIndices) AddPlacedObjects(
        SceneBuilder scene,
        DdmFile ddm,
        PsxLayoutFile psxFile,
        List<string> textureDirs,
        Dictionary<string, MaterialBuilder> materialCache,
        Dictionary<string, byte[]>? ddxTextures)
    {
        var ddmByHash = DdmHashLookup.Build(ddm);
        var meshIndexToDdm = DdmHashLookup.ResolveMeshIndices(psxFile, ddmByHash);

        // Cache built glTF meshes by DDM object index (for instancing —
        // multiple PSX objects can reference the same DDM mesh geometry)
        var meshCache = new Dictionary<int, (MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> Mesh, int Triangles)>();
        var totalTriangles = 0;
        var placedIndices = new HashSet<int>();

        foreach (var psxObj in psxFile.Objects)
        {
            if (psxObj.MeshIndex >= psxFile.MeshNameHashes.Length)
                continue;

            if (!meshIndexToDdm.TryGetValue(psxObj.MeshIndex, out var ddmIdx))
                continue;

            var ddmObj = ddm.Objects[ddmIdx];
            if (ddmObj.Vertices.Count == 0 || ddmObj.Indices.Length == 0)
                continue;

            placedIndices.Add(ddmIdx);

            // Build mesh once, reuse for instances
            if (!meshCache.TryGetValue(ddmIdx, out var cached))
            {
                var gltfMesh = BuildDdmMesh(ddmObj, textureDirs, materialCache, ddxTextures, out var tris);
                cached = (gltfMesh, tris);
                meshCache[ddmIdx] = cached;
                totalTriangles += tris;
            }

            // Place via node transform (DDM coordinate convention: -X, -Y, +Z)
            var translation = new Vector3(-psxObj.X, -psxObj.Y, psxObj.Z);
            scene.AddRigidMesh(cached.Mesh, Matrix4x4.CreateTranslation(translation));
        }

        return (totalTriangles, placedIndices);
    }

    /// <summary>
    /// Adds DDM objects to a scene in local space (no world placement).
    /// Objects whose indices appear in excludeIndices are skipped.
    /// </summary>
    private static int AddUnplacedObjects(
        SceneBuilder scene,
        DdmFile ddm,
        List<string> textureDirs,
        Dictionary<string, MaterialBuilder> materialCache,
        Dictionary<string, byte[]>? ddxTextures,
        HashSet<int>? excludeIndices = null)
    {
        var totalTriangles = 0;

        for (var i = 0; i < ddm.Objects.Count; i++)
        {
            if (excludeIndices != null && excludeIndices.Contains(i))
                continue;

            var obj = ddm.Objects[i];
            if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
                continue;

            var gltfMesh = BuildDdmMesh(obj, textureDirs, materialCache, ddxTextures, out var tris);
            totalTriangles += tris;

            var node = new NodeBuilder(obj.Name);
            scene.AddRigidMesh(gltfMesh, node);
        }

        return totalTriangles;
    }

    /// <summary>
    /// Writes a placed level with up to three output files.
    /// </summary>
    /// <param name="levelDdm">Main level DDM (static geometry).</param>
    /// <param name="objectsDdm">Optional objects DDM (_o file, interactive objects).</param>
    /// <param name="psxFile">Parsed PSX file providing level object positions and mesh name hashes.</param>
    /// <param name="objectsPsxFile">Optional objects PSX (_o.PSX) for object placement. Falls back to psxFile.</param>
    /// <param name="outputDir">Output directory.</param>
    /// <param name="levelName">Level stem name (e.g. "skware").</param>
    /// <param name="texturePath">Optional texture directory.</param>
    /// <param name="ddxPath">Optional directory for DDX texture archives and .lit light files.</param>
    /// <returns>(combined triangles, level triangles, objects triangles)</returns>
    public static (int Combined, int Level, int Objects) WritePlacedLevel(
        DdmFile levelDdm,
        DdmFile? objectsDdm,
        PsxLayoutFile psxFile,
        PsxLayoutFile? objectsPsxFile,
        string outputDir,
        string levelName,
        string? texturePath = null,
        string? ddxPath = null)
    {
        Directory.CreateDirectory(outputDir);
        var textureDirs = BuildTextureSearchPaths(texturePath, levelName);
        var ddxTextures = LoadDdxTextures(ddxPath, levelName);

        // Auto-detect .lit file from the same directory as DDX/DDM/PSX
        var lights = FindAndParseLitFile(levelName, ddxPath);

        var materialCache = new Dictionary<string, MaterialBuilder>();

        // 1. Level-only (placed static geometry)
        var levelScene = new SceneBuilder();
        var (levelTriangles, _) = AddPlacedObjects(levelScene, levelDdm, psxFile,
            textureDirs, materialCache, ddxTextures);
        if (lights != null)
            AddLightsToScene(levelScene, lights);
        var levelPath = Path.Combine(outputDir, levelName + "_level.glb");
        levelScene.ToGltf2().SaveGLB(levelPath);

        // 2. Objects-only (placed via objects PSX, unmatched at origin)
        var objectsTriangles = 0;
        var objPsx = objectsPsxFile ?? psxFile;
        if (objectsDdm != null)
        {
            var objectsScene = new SceneBuilder();
            var objectsMaterialCache = new Dictionary<string, MaterialBuilder>();
            var (placedTris, placedIndices) = AddPlacedObjects(objectsScene, objectsDdm, objPsx,
                textureDirs, objectsMaterialCache, ddxTextures);
            objectsTriangles = placedTris;
            objectsTriangles += AddUnplacedObjects(objectsScene, objectsDdm,
                textureDirs, objectsMaterialCache, ddxTextures, placedIndices);
            var objectsPath = Path.Combine(outputDir, levelName + "_objects.glb");
            objectsScene.ToGltf2().SaveGLB(objectsPath);
        }

        // 3. Combined (placed level + placed/unplaced objects in one file)
        var combinedScene = new SceneBuilder();
        var combinedMaterialCache = new Dictionary<string, MaterialBuilder>();
        var (combinedTriangles, _) = AddPlacedObjects(combinedScene, levelDdm, psxFile,
            textureDirs, combinedMaterialCache, ddxTextures);

        if (objectsDdm != null)
        {
            var (objTris, objPlaced) = AddPlacedObjects(combinedScene, objectsDdm, objPsx,
                textureDirs, combinedMaterialCache, ddxTextures);
            combinedTriangles += objTris;
            combinedTriangles += AddUnplacedObjects(combinedScene, objectsDdm,
                textureDirs, combinedMaterialCache, ddxTextures, objPlaced);
        }

        if (lights != null)
            AddLightsToScene(combinedScene, lights);

        var combinedPath = Path.Combine(outputDir, levelName + ".glb");
        combinedScene.ToGltf2().SaveGLB(combinedPath);

        return (combinedTriangles, levelTriangles, objectsTriangles);
    }

    /// <summary>
    /// Adds parsed .lit lights to a glTF scene using KHR_lights_punctual.
    /// Coordinates are converted from 3ds Max space to glTF space (-X, -Y, +Z).
    /// </summary>
    private static void AddLightsToScene(SceneBuilder scene, List<LitLight> lights)
    {
        foreach (var lit in lights)
        {
            var gltfLight = CreateGltfLight(lit);
            if (gltfLight == null) continue;

            var pos = new Vector3(-lit.Position.X, -lit.Position.Y, lit.Position.Z);
            var node = new NodeBuilder(lit.Name);
            node.LocalTransform = CreateLightTransform(pos, lit.Direction, lit.Type);

            scene.AddLight(gltfLight, node);
        }
    }

    private static LightBuilder? CreateGltfLight(LitLight lit)
    {
        var (color, intensity) = NormalizeHdrColor(lit.Color);
        var range = lit.Atten2 > 0 ? lit.Atten2 : float.PositiveInfinity;

        return lit.Type switch
        {
            LitLightType.Point => new LightBuilder.Point
            {
                Color = color, Intensity = intensity, Range = range,
            },
            LitLightType.Spot => CreateSpotLight(color, intensity, range, lit),
            // DirLights with small Radius are bounded area lights — approximate as spot.
            // Large or negative Radius means unbounded directional.
            LitLightType.Directional when lit.Radius is > 0 and < 100 =>
                CreateSpotLight(color, intensity, range, lit),
            LitLightType.Directional => new LightBuilder.Directional
            {
                Color = color, Intensity = intensity,
            },
            _ => null,
        };
    }

    private static LightBuilder.Spot CreateSpotLight(
        Vector3 color, float intensity, float range, LitLight lit) => new()
    {
        Color = color,
        Intensity = intensity,
        Range = range,
        InnerConeAngle = lit.Hotspot > 0 ? lit.Hotspot / 2f : 0f,
        OuterConeAngle = lit.Radius > 0 ? lit.Radius / 2f : 0.785f,
    };

    private static (Vector3 Color, float Intensity) NormalizeHdrColor(Vector3 color)
    {
        var max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        return max > 1f ? (color / max, max) : (color, 1f);
    }

    /// <summary>
    /// Creates a node transform that positions the light and orients it so that
    /// the glTF local -Z axis points in the light's direction.
    /// </summary>
    private static Matrix4x4 CreateLightTransform(Vector3 position, Vector3? direction, LitLightType type)
    {
        if (direction == null || type == LitLightType.Point)
            return Matrix4x4.CreateTranslation(position);

        // Convert direction from .lit space to glTF space: (-X, -Y, +Z)
        var dir = direction.Value;
        var gltfDir = Vector3.Normalize(new Vector3(-dir.X, -dir.Y, dir.Z));

        // Build rotation: glTF lights point along local -Z, so rotate -Z to gltfDir
        var rotation = RotationFromTo(-Vector3.UnitZ, gltfDir);
        return Matrix4x4.CreateFromQuaternion(rotation)
             * Matrix4x4.CreateTranslation(position);
    }

    private static Quaternion RotationFromTo(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.999999f)
            return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            // 180-degree rotation around any perpendicular axis
            var perp = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var axis = Vector3.Normalize(Vector3.Cross(from, perp));
            return new Quaternion(axis, 0);
        }
        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1 + dot));
    }

    /// <summary>
    /// Loads DDX texture archives for a level into an in-memory cache.
    /// Searches for level DDX and objects DDX archives.
    /// </summary>
    private static Dictionary<string, byte[]>? LoadDdxTextures(string? ddxPath, string levelName)
    {
        if (string.IsNullOrEmpty(ddxPath) || !Directory.Exists(ddxPath))
            return null;

        Dictionary<string, byte[]>? cache = null;

        // Level DDX (e.g. skware.DDX)
        var levelDdx = FindCompanionFile(ddxPath, levelName, ".ddx");
        if (levelDdx != null)
        {
            cache ??= new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            MergeDdxEntries(cache, DdxArchive.ReadAllEntries(levelDdx));
        }

        // Objects DDX (e.g. skware_o.DDX)
        var objectsDdx = FindCompanionFile(ddxPath, levelName + "_o", ".ddx");
        if (objectsDdx != null)
        {
            cache ??= new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            MergeDdxEntries(cache, DdxArchive.ReadAllEntries(objectsDdx));
        }

        return cache;
    }

    private static void MergeDdxEntries(Dictionary<string, byte[]> target, Dictionary<string, byte[]> source)
    {
        foreach (var (key, value) in source)
            target.TryAdd(key, value);
    }

    private static List<LitLight>? FindAndParseLitFile(string levelName, string? searchDir)
    {
        if (string.IsNullOrEmpty(searchDir)) return null;
        var litPath = FindCompanionFile(searchDir, levelName, ".lit");
        if (litPath == null) return null;
        try { return LitFile.Parse(litPath); }
        catch { return null; }
    }

    private static string? FindCompanionFile(string directory, string stem, string extension)
    {
        var files = Directory.GetFiles(directory, stem + extension,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? files[0] : null;
    }
}
