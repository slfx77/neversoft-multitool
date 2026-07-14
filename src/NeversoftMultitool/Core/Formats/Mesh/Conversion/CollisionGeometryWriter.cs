using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     COL collision scenes: grayscale-intensity vertex colours per object.
/// </summary>
internal static class CollisionGeometryWriter
{
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

                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices,
                    indices,
                    MakeCollisionVertex(obj, face.V0),
                    MakeCollisionVertex(obj, face.V1),
                    MakeCollisionVertex(obj, face.V2));
            }
        }

        ModelDocumentGeometryAdapter.AddPrimitive(mesh, "collision", materialIndex, vertices, indices);
        ModelDocumentGeometryAdapter.AddMeshNode(document, "collision", mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
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
}
