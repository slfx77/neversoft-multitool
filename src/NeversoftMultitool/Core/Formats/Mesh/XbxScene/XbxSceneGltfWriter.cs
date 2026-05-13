using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Writes an XbxScene to glTF 2.0 (.glb).
///     Triangle strips from degenerate index buffers, first-pass texture only,
///     rigid mesh (no skinning). Follows Ps2SceneGltfWriter patterns.
/// </summary>
public static class XbxSceneGltfWriter
{
    public static int Write(XbxScene scene, string outputPath, MeshChecksumTextureResolver? textureProvider = null)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var (model, triangles) = Build(scene, textureProvider);
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    internal static (ModelRoot Model, int Triangles) Build(
        XbxScene scene, MeshChecksumTextureResolver? textureProvider = null)
    {
        var sceneBuilder = new SceneBuilder();
        var materialCache = new Dictionary<uint, MaterialBuilder>();
        var totalTriangles = 0;

        foreach (var sector in scene.Sectors)
        {
            foreach (var mesh in sector.Meshes)
            {
                if (mesh.Vertices.Length < 3 || mesh.FaceIndices.Length < 3)
                    continue;

                var mat = GetOrCreateMaterial(
                    materialCache, scene.Materials, mesh.MaterialChecksum, textureProvider);

                var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("mesh");
                var prim = meshBuilder.UsePrimitive(mat);

                totalTriangles += mesh.IsPreTriangulated
                    ? AddIndexedTriangles(prim, mesh)
                    : AddTriangleStrip(prim, mesh);

                if (totalTriangles > 0 || meshBuilder.Primitives.Count > 0)
                    sceneBuilder.AddRigidMesh(meshBuilder, Matrix4x4.Identity);
            }
        }

        return (sceneBuilder.ToGltf2(), totalTriangles);
    }

    /// <summary>
    ///     Add pre-triangulated indexed triangles (every 3 indices = one triangle).
    ///     Used for THAW meshes where strips are pre-triangulated during parsing.
    /// </summary>
    private static int AddIndexedTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        XbxMesh mesh)
    {
        var indices = mesh.FaceIndices;
        var verts = mesh.Vertices;
        var count = 0;

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];

            if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length)
                continue;

            prim.AddTriangle(
                MakeVertex(verts[i0]),
                MakeVertex(verts[i1]),
                MakeVertex(verts[i2]));
            count++;
        }

        return count;
    }

    /// <summary>
    ///     Convert degenerate triangle strip to indexed triangles.
    ///     Degenerate = any two indices equal → strip restart.
    /// </summary>
    private static int AddTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        XbxMesh mesh)
    {
        var indices = mesh.FaceIndices;
        var verts = mesh.Vertices;
        var count = 0;

        for (var i = 2; i < indices.Length; i++)
        {
            var i0 = indices[i - 2];
            var i1 = indices[i - 1];
            var i2 = indices[i];

            // Degenerate triangle = strip restart
            if (i0 == i1 || i1 == i2 || i0 == i2)
                continue;

            // Bounds check
            if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length)
                continue;

            // Alternate winding for proper face orientation
            if (i % 2 == 0)
            {
                prim.AddTriangle(
                    MakeVertex(verts[i0]),
                    MakeVertex(verts[i1]),
                    MakeVertex(verts[i2]));
            }
            else
            {
                prim.AddTriangle(
                    MakeVertex(verts[i1]),
                    MakeVertex(verts[i0]),
                    MakeVertex(verts[i2]));
            }

            count++;
        }

        return count;
    }

    private static (VertexPositionNormal geo, VertexColor1Texture1 mat) MakeVertex(XbxVertex v)
    {
        var pos = v.Position;
        var normal = v.HasNormal ? Vector3.Normalize(v.Normal) : Vector3.UnitY;
        if (float.IsNaN(normal.X)) normal = Vector3.UnitY;

        var color = v.HasColor
            ? new Vector4(
                Math.Min(v.Color.X, 1f),
                Math.Min(v.Color.Y, 1f),
                Math.Min(v.Color.Z, 1f),
                Math.Min(v.Color.W, 1f))
            : Vector4.One;

        // Xbox/DirectX UVs: V=0 at top, same convention as glTF — no flip needed
        var uv = v.TexCoord;

        return (new VertexPositionNormal(pos, normal), new VertexColor1Texture1(color, uv));
    }

    private static MaterialBuilder GetOrCreateMaterial(
        Dictionary<uint, MaterialBuilder> cache,
        XbxMaterial[] materials,
        uint materialChecksum,
        MeshChecksumTextureResolver? textureProvider)
    {
        if (cache.TryGetValue(materialChecksum, out var existing))
            return existing;

        var matName = $"mat_{materialChecksum:X8}";
        var builder = new MaterialBuilder(matName)
            .WithUnlitShader()
            .WithBaseColor(Vector4.One)
            .WithDoubleSide(true);

        // Find the material definition
        XbxMaterial? mat = null;
        foreach (var m in materials)
        {
            if (m.Checksum == materialChecksum)
            {
                mat = m;
                break;
            }
        }

        // Texture from first pass
        if (textureProvider != null && mat?.Passes.Length > 0)
        {
            var texChecksum = mat.Passes[0].TextureChecksum;
            if (texChecksum != 0)
            {
                var pngBytes = textureProvider(texChecksum);
                if (pngBytes != null)
                {
                    var memImage = new MemoryImage(pngBytes);
                    builder.WithChannelImage(KnownChannel.BaseColor, memImage);
                }
            }
        }

        // Alpha handling
        if (mat != null)
        {
            if (mat.AlphaCutoff >= 1)
                builder.WithAlpha(AlphaMode.MASK, mat.AlphaCutoff / 255f);
            else if (mat.Sorted)
                builder.WithAlpha(AlphaMode.BLEND);
        }

        cache[materialChecksum] = builder;
        return builder;
    }
}
