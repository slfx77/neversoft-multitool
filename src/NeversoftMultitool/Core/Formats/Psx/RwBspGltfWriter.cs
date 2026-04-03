using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Psx;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
///     Writes parsed RenderWare BSP (World) data to glTF 2.0 (.glb) files.
///     Used for THPS3 PS2 level BSP files.
///     All atomic sections are merged into a single mesh, grouped by material.
/// </summary>
public static class RwBspGltfWriter
{
    /// <summary>
    ///     Writes a parsed BSP world to a .glb file.
    /// </summary>
    /// <returns>Total number of triangles written.</returns>
    public static int Write(RwBspWorld world, string outputPath,
        RwDffGltfWriter.TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var (model, triangles) = Build(world, textureProvider);
        if (triangles == 0) return 0;
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    internal static (SharpGLTF.Schema2.ModelRoot Model, int Triangles) Build(
        RwBspWorld world, RwDffGltfWriter.TextureProvider? textureProvider = null)
    {
        var scene = new SceneBuilder();
        var materialCache = new Dictionary<string, MaterialBuilder>(StringComparer.OrdinalIgnoreCase);
        var totalTriangles = 0;

        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("level");

        foreach (var section in world.Sections)
            totalTriangles += AddSection(section, world.Materials, gltfMesh, materialCache, textureProvider);

        if (totalTriangles > 0)
        {
            var rootNode = new NodeBuilder("world");
            scene.AddRigidMesh(gltfMesh, rootNode);
        }

        return (scene.ToGltf2(), totalTriangles);
    }

    private static int AddSection(RwBspSection section, RwMaterial[] materials,
        MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> gltfMesh,
        Dictionary<string, MaterialBuilder> materialCache,
        RwDffGltfWriter.TextureProvider? textureProvider)
    {
        if (section.Vertices.Length == 0 || section.Triangles.Length == 0)
            return 0;

        // Group triangles by material index
        var trisByMaterial = new Dictionary<int, List<RwTriangle>>();
        foreach (var tri in section.Triangles)
        {
            if (!trisByMaterial.TryGetValue(tri.MaterialIndex, out var list))
            {
                list = [];
                trisByMaterial[tri.MaterialIndex] = list;
            }

            list.Add(tri);
        }

        var triangleCount = 0;
        foreach (var (matIndex, triangles) in trisByMaterial)
        {
            // Resolve material from the World's shared list (with matListWindowBase offset)
            var globalMatIndex = section.MatListWindowBase + matIndex;
            if (globalMatIndex < 0 || globalMatIndex >= materials.Length)
                continue;

            var rwMat = materials[globalMatIndex];

            // Skip untextured materials (collision/triggers) and debug wireframe textures.
            if (string.IsNullOrEmpty(rwMat.TextureName))
                continue;

            var texBaseName = Path.GetFileNameWithoutExtension(rwMat.TextureName);
            if (IsDevTexture(texBaseName))
                continue;

            var material = GetOrCreateMaterial(rwMat, materialCache, textureProvider);
            var prim = gltfMesh.UsePrimitive(material);

            foreach (var tri in triangles)
            {
                var va = MakeVertex(section, tri.V0);
                var vb = MakeVertex(section, tri.V1);
                var vc = MakeVertex(section, tri.V2);
                prim.AddTriangle(va, vb, vc);
            }

            triangleCount += triangles.Count;
        }

        return triangleCount;
    }

    private static VERTEX MakeVertex(RwBspSection section, int index)
    {
        var pos = index < section.Vertices.Length
            ? section.Vertices[index]
            : Vector3.Zero;

        var normal = Vector3.UnitY;
        if (section.Normals != null && index < section.Normals.Length)
        {
            var n = section.Normals[index];
            var len = n.Length();
            normal = len > 0.001f ? n / len : Vector3.UnitY;
        }

        var color = Vector4.One;
        if (section.Colors != null && index < section.Colors.Length)
        {
            var c = section.Colors[index];
            color = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        }

        var uv = Vector2.Zero;
        if (section.UVs != null && index < section.UVs.Length)
            uv = section.UVs[index];

        return new VERTEX(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(color, uv));
    }

    private static MaterialBuilder GetOrCreateMaterial(
        RwMaterial material,
        Dictionary<string, MaterialBuilder> cache,
        RwDffGltfWriter.TextureProvider? textureProvider)
    {
        // Cache key includes GsAlpha so the same texture with different blend modes gets separate materials
        var key = material.TextureName ?? $"mat_{material.R}_{material.G}_{material.B}_{material.A}";
        if (material.GsAlpha != 0)
            key += $"_gs{material.GsAlpha:X2}";

        if (cache.TryGetValue(key, out var existing))
            return existing;

        var builder = new MaterialBuilder(key)
            .WithUnlitShader()
            .WithDoubleSide(true);

        var baseColor = new Vector4(
            material.R / 255f,
            material.G / 255f,
            material.B / 255f,
            material.A / 255f);
        builder.WithBaseColor(baseColor);

        var textureHasAlpha = false;
        if (textureProvider != null && !string.IsNullOrEmpty(material.TextureName))
        {
            var pngBytes = textureProvider(material.TextureName);
            if (pngBytes != null)
            {
                // Texture processing depends on blend mode:
                // - Additive: dark pixels → transparent, bright → opaque white overlay
                // - Subtractive: dark pixels → transparent, bright → opaque black shadow
                // - Other: color-key magenta (255,0,255) backgrounds to alpha transparency
                if (material.IsAdditive)
                {
                    pngBytes = GltfTextureHelper.ConvertBlendTexture(pngBytes, 255, 255, 255);
                    textureHasAlpha = true;
                }
                else if (material.IsSubtractive)
                {
                    pngBytes = GltfTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0);
                    textureHasAlpha = true;
                }
                else
                {
                    (pngBytes, textureHasAlpha) = GltfTextureHelper.ApplyColorKey(pngBytes);
                }

                builder.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(pngBytes));
            }
        }

        // Alpha mode based on decoded PS2 GS ALPHA blend formula:
        // - IsBlend (formula uses Cd): 0x44, 0x64, additive, subtractive → BLEND
        // - Translucent material color (A < 255) → BLEND
        // - Texture has alpha (from TXD palette, magenta color-key, or blend conversion) → MASK
        // - Degenerate values (0x0A, 0x20, 0x2A etc. → formula = Cs = opaque) → OPAQUE
        if (material.A < 255 || material.IsBlend)
            builder.WithAlpha(AlphaMode.BLEND);
        else if (textureHasAlpha)
            builder.WithAlpha(AlphaMode.MASK);

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    ///     Returns true for dev/debug textures that should be excluded from glTF output
    ///     (collision volumes, trigger planes, wireframe placeholders).
    /// </summary>
    private static bool IsDevTexture(string name)
    {
        return string.Equals(name, "wire", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "transparent", StringComparison.OrdinalIgnoreCase)
               || name.Contains("_CR_Collision_", StringComparison.OrdinalIgnoreCase)
               || name.Contains("_CR_TriggerPlane_", StringComparison.OrdinalIgnoreCase)
               || name.Contains("_CR_VertPoly", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Builds a texture provider from an RW TXD file.
    /// </summary>
    public static RwDffGltfWriter.TextureProvider BuildTxdTextureProvider(Ps2TexResult txdResult)
    {
        return RwDffGltfWriter.BuildTxdTextureProvider(txdResult);
    }
}
