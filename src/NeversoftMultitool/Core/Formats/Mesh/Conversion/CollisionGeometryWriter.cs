using System.Numerics;
using NeversoftMultitool.Core.Formats.Collision;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     COL collision scenes: grayscale-intensity vertex colours per object.
/// </summary>
internal static class CollisionGeometryWriter
{
    public static void PopulateCollision(
        ModelDocument document,
        ColScene scene,
        bool asOverlay = false)
    {
        var materialIndex = document.Materials.Count;
        document.Materials.Add(new RenderMaterial
        {
            Name = asOverlay ? "collision_overlay" : "collision",
            BaseColor = asOverlay
                ? new Vector4(1f, 0.28f, 0.04f, 0.38f)
                : new Vector4(0.7f, 0.7f, 0.7f, 1f),
            AlphaMode = asOverlay ? ModelAlphaMode.Blend : ModelAlphaMode.Opaque,
            DoubleSided = true,
            Unlit = true
        });

        var name = asOverlay ? "collision_overlay" : "collision";
        var mesh = new ModelMesh { Name = name };
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
                    MakeCollisionVertex(obj, face.V0, asOverlay),
                    MakeCollisionVertex(obj, face.V1, asOverlay),
                    MakeCollisionVertex(obj, face.V2, asOverlay));
            }
        }

        ModelDocumentGeometryAdapter.AddPrimitive(mesh, name, materialIndex, vertices, indices);
        ModelDocumentGeometryAdapter.AddMeshNode(document, name, mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static ModelVertex MakeCollisionVertex(ColObject obj, int index, bool asOverlay)
    {
        if (asOverlay)
        {
            return new ModelVertex(
                obj.Vertices[index],
                Vector3.UnitY,
                Vector4.One,
                Vector2.Zero);
        }

        var colorOffset = index * 4;
        if (colorOffset >= 0 && colorOffset + 3 < obj.VertexColorsRgba.Length)
        {
            var rgba = obj.VertexColorsRgba;
            return new ModelVertex(
                obj.Vertices[index],
                Vector3.UnitY,
                new Vector4(
                    rgba[colorOffset] / 255f,
                    rgba[colorOffset + 1] / 255f,
                    rgba[colorOffset + 2] / 255f,
                    rgba[colorOffset + 3] / 255f),
                Vector2.Zero);
        }

        var intensity = index < obj.Intensities.Length ? obj.Intensities[index] / 255f : 1f;
        return new ModelVertex(
            obj.Vertices[index],
            Vector3.UnitY,
            new Vector4(intensity, intensity, intensity, 1f),
            Vector2.Zero);
    }
}
