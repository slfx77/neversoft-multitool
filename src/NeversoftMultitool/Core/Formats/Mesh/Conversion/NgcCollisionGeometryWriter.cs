using System.Numerics;
using NeversoftMultitool.Core.Formats.Collision;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>Emits a GameCube COL after its external position pool is proven.</summary>
internal static class NgcCollisionGeometryWriter
{
    public static int Populate(
        ModelDocument document,
        NgcColScene collision,
        NgcCollisionPositionBinding binding,
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

        var before = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Sum(static primitive => primitive.TriangleCount);
        foreach (var obj in collision.Objects)
        {
            var name = asOverlay
                ? $"collision_overlay_{obj.Checksum:X8}"
                : $"collision_{obj.Checksum:X8}";
            var mesh = new ModelMesh { Name = name };
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            for (var faceIndex = 0; faceIndex < obj.Faces.Length; faceIndex++)
            {
                var face = obj.Faces[faceIndex];
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices,
                    indices,
                    MakeVertex(collision, binding, obj.FirstFaceIndex + faceIndex, 0, face.V0, asOverlay),
                    MakeVertex(collision, binding, obj.FirstFaceIndex + faceIndex, 1, face.V1, asOverlay),
                    MakeVertex(collision, binding, obj.FirstFaceIndex + faceIndex, 2, face.V2, asOverlay));
            }

            ModelDocumentGeometryAdapter.AddPrimitive(mesh, "triangles", materialIndex, vertices, indices);
            ModelDocumentGeometryAdapter.AddMeshNode(document, name, mesh);
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        return document.TriangleCount - before;
    }

    private static ModelVertex MakeVertex(
        NgcColScene collision,
        NgcCollisionPositionBinding binding,
        int globalFaceIndex,
        int corner,
        int vertexIndex,
        bool asOverlay)
    {
        var intensityIndex = checked(globalFaceIndex * 3 + corner);
        var intensity = asOverlay || (uint)intensityIndex >= (uint)collision.CornerIntensities.Length
            ? 1f
            : collision.CornerIntensities[intensityIndex] / 255f;
        return new ModelVertex(
            binding.Positions[vertexIndex],
            Vector3.UnitY,
            new Vector4(intensity, intensity, intensity, 1f),
            Vector2.Zero);
    }
}
