using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Emits the PSX collision shell owned by a THPS2X DDM level in the DDM
///     document's coordinate system. THPS2X retained the PSX world as its
///     collision representation while replacing visible geometry with DDM.
/// </summary>
internal static class Thps2XPsxCollisionGeometryWriter
{
    /// <summary>
    ///     Adds every structurally valid PSX face, including the opaque bit-7
    ///     faces hidden by the ordinary display path. Returns the number of
    ///     non-degenerate triangles actually added.
    /// </summary>
    internal static int PopulateOverlay(ModelDocument document, PsxMeshFile collision)
    {
        var materialIndex = document.Materials.Count;
        document.Materials.Add(new RenderMaterial
        {
            Name = "collision_overlay",
            BaseColor = new Vector4(1f, 0.28f, 0.04f, 0.38f),
            AlphaMode = ModelAlphaMode.Blend,
            DoubleSided = true,
            Unlit = true
        });

        var mesh = new ModelMesh { Name = "collision_overlay" };
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();

        foreach (var obj in collision.Objects)
        {
            if (obj.MeshIndex >= collision.Meshes.Count)
                continue;

            var sourceMesh = collision.Meshes[obj.MeshIndex];
            var spriteResolver = PsxSpriteVertexResolver.TryCreate(sourceMesh);
            var objectOffset = new Vector3(
                -obj.RawX / 4096f,
                -obj.RawY / 4096f,
                obj.RawZ / 4096f);

            AddFaces(sourceMesh.Faces);
            AddFaces(sourceMesh.InvisibleFaces);

            void AddFaces(IEnumerable<PsxFace> faces)
            {
                foreach (var face in faces)
                {
                    if (!HasValidVertexIndices(sourceMesh, face))
                        continue;

                    var v0 = MakeVertex(face, 0);
                    var v1 = MakeVertex(face, 1);
                    var v2 = MakeVertex(face, 2);

                    // PSX face slots are clockwise under either supported
                    // handedness map. Reverse them for glTF CCW winding.
                    ModelDocumentGeometryAdapter.AddTriangle(
                        vertices, indices, v0, v2, v1);

                    if (face.IsQuad)
                    {
                        var v3 = MakeVertex(face, 3);
                        ModelDocumentGeometryAdapter.AddTriangle(
                            vertices, indices, v1, v2, v3);
                    }
                }
            }

            ModelVertex MakeVertex(PsxFace face, int slot)
            {
                var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
                Vector3 position;
                if (spriteResolver != null
                    && spriteResolver.TryResolvePosition(vertexIndex, out var spritePosition))
                {
                    // The resolver returns the ordinary PSX glTF basis
                    // (X,-Y,-Z). Convert it to the DDM basis (-X,-Y,+Z).
                    position = new Vector3(
                        -spritePosition.X,
                        spritePosition.Y,
                        -spritePosition.Z) * collision.ScaleDivisor;
                }
                else
                {
                    var vertex = sourceMesh.Vertices[(int)vertexIndex];
                    position = new Vector3(-vertex.X, -vertex.Y, vertex.Z)
                               * collision.ScaleDivisor;
                }

                // The general PSX renderer applies the runtime's 2.25 world
                // scale. DDM and its paired collision/layout file are authored
                // in serialized units instead: NxTools imports both the raw
                // i16 vertices and raw 20.12 placements directly. Restoring
                // ScaleDivisor above and using raw/4096 here makes both layers
                // share DDMGeometryWriter's (-X,-Y,+Z) world exactly.
                return CollisionVertex(position + objectOffset);
            }
        }

        ModelDocumentGeometryAdapter.AddPrimitive(
            mesh, "collision_overlay", materialIndex, vertices, indices);
        ModelDocumentGeometryAdapter.AddMeshNode(document, "collision_overlay", mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        return indices.Count / 3;
    }

    private static ModelVertex CollisionVertex(Vector3 position) =>
        new(position, Vector3.UnitY, Vector4.One, Vector2.Zero);

    private static bool HasValidVertexIndices(PsxMesh mesh, PsxFace face)
    {
        var count = (uint)mesh.Vertices.Count;
        return face.Index0 < count
               && face.Index1 < count
               && face.Index2 < count
               && (!face.IsQuad || face.Index3 < count);
    }
}
