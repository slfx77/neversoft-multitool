using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     RenderWare BSP worlds: atomic-section triangle emission over the shared
///     RW material path.
/// </summary>
internal static class RwBspGeometryWriter
{
    public static void PopulateRwBsp(
        ModelDocument document,
        RwBspWorld world,
        MeshNamedTextureResolver? textureProvider)
    {
        for (var i = 0; i < world.Materials.Length && i < document.Materials.Count; i++)
            RwGeometryWriter.ApplyRwMaterial(document, document.Materials[i], world.Materials[i], textureProvider,
                true);

        var mesh = new ModelMesh { Name = "level" };
        foreach (var group in world.Sections
                     .SelectMany(section => section.Triangles.Select(tri => (section, tri)))
                     .GroupBy(item => item.section.MatListWindowBase + item.tri.MaterialIndex))
        {
            var materialIndex = group.Key;
            if (materialIndex < 0 || materialIndex >= world.Materials.Length)
                continue;

            var rwMaterial = world.Materials[materialIndex];
            if (string.IsNullOrEmpty(rwMaterial.TextureName) ||
                RwGeometryWriter.IsRwDevTexture(Path.GetFileNameWithoutExtension(rwMaterial.TextureName)))
            {
                continue;
            }

            if (materialIndex >= document.Materials.Count)
                materialIndex = RwGeometryWriter.AddRwMaterial(document, rwMaterial, textureProvider, true);

            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var (section, tri) in group)
            {
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices,
                    indices,
                    MakeRwBspVertex(section, tri.V0),
                    MakeRwBspVertex(section, tri.V1),
                    MakeRwBspVertex(section, tri.V2));
            }

            ModelDocumentGeometryAdapter.AddPrimitive(mesh, $"mat_{group.Key:D3}", materialIndex, vertices, indices);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, "world", mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static ModelVertex MakeRwBspVertex(RwBspSection section, int index)
    {
        var position = index < section.Vertices.Length ? section.Vertices[index] : Vector3.Zero;
        var normal = section.Normals != null && index < section.Normals.Length
            ? ModelDocumentGeometryAdapter.NormalizeOrDefault(section.Normals[index])
            : Vector3.UnitY;
        var color = section.Colors != null && index < section.Colors.Length
            ? RwGeometryWriter.ToColor(section.Colors[index])
            : Vector4.One;
        var uv = section.UVs != null && index < section.UVs.Length ? section.UVs[index] : Vector2.Zero;
        return new ModelVertex(position, normal, color, uv);
    }
}
