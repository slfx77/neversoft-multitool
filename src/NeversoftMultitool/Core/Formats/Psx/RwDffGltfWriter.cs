using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Psx;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
///     Writes parsed RenderWare DFF (Clump) data to glTF 2.0 (.glb) files.
///     Used for THPS3 PS2 .SKN mesh files.
///     Rigid mesh only (no skinning support yet).
/// </summary>
public static class RwDffGltfWriter
{
    /// <summary>
    ///     Resolves a texture name to PNG bytes for embedding in glTF.
    ///     Returns null if the texture cannot be resolved.
    /// </summary>
    public delegate byte[]? TextureProvider(string textureName);

    /// <summary>
    ///     Writes a parsed DFF clump to a .glb file.
    /// </summary>
    /// <returns>Total number of triangles written.</returns>
    public static int Write(RwDffClump clump, string outputPath,
        TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<string, MaterialBuilder>(StringComparer.OrdinalIgnoreCase);
        var totalTriangles = 0;

        // Build frame hierarchy as NodeBuilder tree
        var frameNodes = BuildFrameHierarchy(clump.Frames);

        // Each Atomic links a frame to a geometry
        foreach (var atomic in clump.Atomics)
        {
            if (atomic.GeometryIndex < 0 || atomic.GeometryIndex >= clump.Geometries.Length)
                continue;

            var geometry = clump.Geometries[atomic.GeometryIndex];
            if (geometry.Vertices.Length == 0 || geometry.Triangles.Length == 0)
                continue;

            var frameNode = atomic.FrameIndex >= 0 && atomic.FrameIndex < frameNodes.Length
                ? frameNodes[atomic.FrameIndex]
                : new NodeBuilder($"atomic_{atomic.GeometryIndex}");

            var gltfMesh = BuildMesh(geometry, materialCache, textureProvider,
                $"geom_{atomic.GeometryIndex}");

            totalTriangles += geometry.Triangles.Length;
            scene.AddRigidMesh(gltfMesh, frameNode);
        }

        if (totalTriangles == 0)
            return 0;

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
    }

    private static NodeBuilder[] BuildFrameHierarchy(RwFrame[] frames)
    {
        var nodes = new NodeBuilder[frames.Length];

        for (var i = 0; i < frames.Length; i++)
        {
            var frame = frames[i];
            var name = $"frame_{i}";

            if (frame.ParentIndex >= 0 && frame.ParentIndex < i && nodes[frame.ParentIndex] != null)
            {
                nodes[i] = nodes[frame.ParentIndex].CreateNode(name);
            }
            else
            {
                nodes[i] = new NodeBuilder(name);
            }

            // Set local transform from frame matrix
            nodes[i].LocalMatrix = frame.LocalTransform;
        }

        return nodes;
    }

    private static MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> BuildMesh(
        RwGeometry geometry,
        Dictionary<string, MaterialBuilder> materialCache,
        TextureProvider? textureProvider,
        string meshName)
    {
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(meshName);

        // Group triangles by material index
        var trisByMaterial = new Dictionary<int, List<RwTriangle>>();
        foreach (var tri in geometry.Triangles)
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
            var material = matIndex >= 0 && matIndex < geometry.Materials.Length
                ? GetOrCreateMaterial(geometry.Materials[matIndex], materialCache, textureProvider)
                : GetDefaultMaterial(materialCache);

            var prim = gltfMesh.UsePrimitive(material);

            foreach (var tri in triangles)
            {
                var va = MakeVertex(geometry, tri.V0);
                var vb = MakeVertex(geometry, tri.V1);
                var vc = MakeVertex(geometry, tri.V2);
                prim.AddTriangle(va, vb, vc);
            }
        }

        return gltfMesh;
    }

    private static VERTEX MakeVertex(RwGeometry geometry, int index)
    {
        var pos = index < geometry.Vertices.Length
            ? geometry.Vertices[index]
            : Vector3.Zero;

        var normal = Vector3.UnitY;
        if (geometry.Normals != null && index < geometry.Normals.Length)
        {
            var n = geometry.Normals[index];
            var len = n.Length();
            normal = len > 0.001f ? n / len : Vector3.UnitY;
        }

        // Vertex colors: RW uses 0-255 range, normalize to 0-1
        var color = Vector4.One;
        if (geometry.Colors != null && index < geometry.Colors.Length)
        {
            var c = geometry.Colors[index];
            color = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        }

        // UVs: already V-flipped during parsing
        var uv = Vector2.Zero;
        if (geometry.UVs != null && index < geometry.UVs.Length)
            uv = geometry.UVs[index];

        return new VERTEX(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(color, uv));
    }

    private static MaterialBuilder GetOrCreateMaterial(
        RwMaterial material,
        Dictionary<string, MaterialBuilder> cache,
        TextureProvider? textureProvider)
    {
        var key = material.TextureName ?? $"mat_{material.R}_{material.G}_{material.B}_{material.A}";

        if (cache.TryGetValue(key, out var existing))
            return existing;

        var builder = new MaterialBuilder(key)
            .WithUnlitShader()
            .WithDoubleSide(true);

        // Set base color from material color
        var baseColor = new Vector4(
            material.R / 255f,
            material.G / 255f,
            material.B / 255f,
            material.A / 255f);
        builder.WithBaseColor(baseColor);

        // Embed texture if available
        if (textureProvider != null && !string.IsNullOrEmpty(material.TextureName))
        {
            var pngBytes = textureProvider(material.TextureName);
            if (pngBytes != null)
            {
                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);
            }
        }

        // Alpha handling
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
    ///     Builds a texture provider from an RW TXD file parsed via RwTxdFile.
    ///     Resolves texture names to PNG bytes using the parsed texture dictionary.
    /// </summary>
    public static TextureProvider BuildTxdTextureProvider(Ps2TexResult txdResult)
    {
        // Build name → texture lookup (RW TXD textures have Name set during parsing)
        var lookup = new Dictionary<string, Ps2Texture>(StringComparer.OrdinalIgnoreCase);
        foreach (var tex in txdResult.Textures)
        {
            if (tex.Pixels != null && tex.Name != null)
                lookup.TryAdd(tex.Name, tex);
        }

        return textureName =>
        {
            // Try exact match first, then strip file extension (TXD stores names without extension)
            if (!lookup.TryGetValue(textureName, out var tex))
            {
                var extIdx = textureName.LastIndexOf('.');
                if (extIdx <= 0 || !lookup.TryGetValue(textureName[..extIdx], out tex))
                    return null;
            }

            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels!);
        };
    }
}
