using System.Numerics;
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

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<string, MaterialBuilder>(StringComparer.OrdinalIgnoreCase);
        var totalTriangles = 0;

        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("level");

        // Merge all sections into a single mesh, grouped by material
        foreach (var section in world.Sections)
        {
            if (section.Vertices.Length == 0 || section.Triangles.Length == 0)
                continue;

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

            foreach (var (matIndex, triangles) in trisByMaterial)
            {
                // Resolve material from the World's shared list (with matListWindowBase offset)
                var globalMatIndex = section.MatListWindowBase + matIndex;
                var material = globalMatIndex >= 0 && globalMatIndex < world.Materials.Length
                    ? GetOrCreateMaterial(world.Materials[globalMatIndex], materialCache, textureProvider)
                    : GetDefaultMaterial(materialCache);

                var prim = gltfMesh.UsePrimitive(material);

                foreach (var tri in triangles)
                {
                    var va = MakeVertex(section, tri.V0);
                    var vb = MakeVertex(section, tri.V1);
                    var vc = MakeVertex(section, tri.V2);
                    prim.AddTriangle(va, vb, vc);
                }

                totalTriangles += triangles.Count;
            }
        }

        if (totalTriangles == 0)
            return 0;

        var rootNode = new NodeBuilder("world");
        scene.AddRigidMesh(gltfMesh, rootNode);

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
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
        var key = material.TextureName ?? $"mat_{material.R}_{material.G}_{material.B}_{material.A}";

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

        if (textureProvider != null && !string.IsNullOrEmpty(material.TextureName))
        {
            var pngBytes = textureProvider(material.TextureName);
            if (pngBytes != null)
            {
                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);
            }
        }

        if (material.A < 255)
            builder.WithAlpha(AlphaMode.BLEND);

        cache[key] = builder;
        return builder;
    }

    private static MaterialBuilder GetDefaultMaterial(Dictionary<string, MaterialBuilder> cache)
    {
        const string key = "__default__";
        if (cache.TryGetValue(key, out var existing))
            return existing;

        var builder = new MaterialBuilder(key)
            .WithUnlitShader()
            .WithBaseColor(Vector4.One)
            .WithDoubleSide(true);

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    ///     Builds a texture provider from an RW TXD file.
    /// </summary>
    public static RwDffGltfWriter.TextureProvider BuildTxdTextureProvider(Ps2TexResult txdResult)
    {
        return RwDffGltfWriter.BuildTxdTextureProvider(txdResult);
    }
}
