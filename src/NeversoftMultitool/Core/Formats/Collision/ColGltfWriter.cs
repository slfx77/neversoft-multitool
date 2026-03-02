using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace NeversoftMultitool.Core.Formats.Collision;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
///     Writes parsed collision data to glTF 2.0 (.glb) files.
///     Each collision object becomes a separate mesh primitive with a shared material.
///     Vertex intensity values are mapped to grayscale vertex colors.
/// </summary>
public static class ColGltfWriter
{
    /// <summary>
    ///     Writes a parsed collision scene to a .glb file.
    /// </summary>
    /// <returns>Total number of triangles written.</returns>
    public static int Write(ColScene scene, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var gltfScene = new SceneBuilder();
        var material = new MaterialBuilder("collision")
            .WithDoubleSide(true)
            .WithMetallicRoughnessShader()
            .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA, new Vector4(0.7f, 0.7f, 0.7f, 1.0f));

        var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("collision");
        var prim = mesh.UsePrimitive(material);
        var totalTriangles = 0;

        foreach (var obj in scene.Objects)
        {
            if (obj.Vertices.Length == 0 || obj.Faces.Length == 0)
                continue;

            foreach (var face in obj.Faces)
            {
                if (face.V0 >= obj.Vertices.Length ||
                    face.V1 >= obj.Vertices.Length ||
                    face.V2 >= obj.Vertices.Length)
                    continue;

                var v0 = MakeVertex(obj, face.V0);
                var v1 = MakeVertex(obj, face.V1);
                var v2 = MakeVertex(obj, face.V2);

                prim.AddTriangle(v0, v1, v2);
                totalTriangles++;
            }
        }

        if (totalTriangles > 0)
        {
            gltfScene.AddRigidMesh(mesh, Matrix4x4.Identity);
            var model = gltfScene.ToGltf2();
            model.SaveGLB(outputPath);
        }

        return totalTriangles;
    }

    private static VERTEX MakeVertex(ColObject obj, int index)
    {
        var pos = obj.Vertices[index];
        // Intensity → grayscale vertex color (0=black, 255=white)
        var intensity = index < obj.Intensities.Length ? obj.Intensities[index] / 255f : 1f;
        var color = new Vector4(intensity, intensity, intensity, 1f);

        return new VERTEX(
            new VertexPositionNormal(pos, Vector3.UnitY),
            new VertexColor1Texture1(color, Vector2.Zero)
        );
    }
}
