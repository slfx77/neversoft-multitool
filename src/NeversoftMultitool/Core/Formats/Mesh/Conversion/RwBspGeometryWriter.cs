using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     RenderWare BSP worlds: atomic-section triangle emission over the shared
///     RW material path. Optional material offsets and name/metadata tags let
///     the THPS3 level loader append its separately-authored sky world without
///     merging either world's TXD/material namespace.
/// </summary>
internal static class RwBspGeometryWriter
{
    public static void PopulateRwBsp(
        ModelDocument document,
        RwBspWorld world,
        MeshNamedTextureResolver? textureProvider,
        int materialStartIndex = 0,
        string? namePrefix = null,
        NativeRenderMetadata? primitiveMetadata = null,
        string? textureNamePrefix = null,
        bool includeUntexturedMaterials = false)
    {
        if (materialStartIndex < 0 || materialStartIndex > document.Materials.Count)
            throw new ArgumentOutOfRangeException(nameof(materialStartIndex));

        var prefix = namePrefix ?? string.Empty;
        for (var i = 0;
             i < world.Materials.Length && materialStartIndex + i < document.Materials.Count;
             i++)
        {
            RwGeometryWriter.ApplyRwMaterial(
                document,
                document.Materials[materialStartIndex + i],
                world.Materials[i],
                textureProvider,
                true,
                textureNamePrefix);
        }

        var mesh = new ModelMesh { Name = prefix + "level" };
        foreach (var group in world.Sections
                     .SelectMany(section => section.Triangles.Select(tri => (section, tri)))
                     .GroupBy(item => item.section.MatListWindowBase + item.tri.MaterialIndex))
        {
            var localMaterialIndex = group.Key;
            if (localMaterialIndex < 0 || localMaterialIndex >= world.Materials.Length)
                continue;

            var rwMaterial = world.Materials[localMaterialIndex];
            if ((!includeUntexturedMaterials && string.IsNullOrEmpty(rwMaterial.TextureName)) ||
                (!string.IsNullOrEmpty(rwMaterial.TextureName) &&
                 RwGeometryWriter.IsRwDevTexture(Path.GetFileNameWithoutExtension(rwMaterial.TextureName))))
            {
                continue;
            }

            var materialIndex = materialStartIndex + localMaterialIndex;
            if (materialIndex >= document.Materials.Count)
            {
                materialIndex = RwGeometryWriter.AddRwMaterial(
                    document, rwMaterial, textureProvider, true, textureNamePrefix);
            }

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

            var primitive = ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, $"mat_{group.Key:D3}", materialIndex, vertices, indices);
            if (primitive != null && primitiveMetadata != null)
                primitive.NativeMetadata.Add(primitiveMetadata);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, prefix + "world", mesh);
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
