using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Emits the exact THPS3 RenderWare BSP collision surface. The shipped BSP
///     stores one collision flag beside every render triangle, so this view uses
///     the world's own vertices and triangle order instead of trying to match a
///     second geometry file.
/// </summary>
internal static class RwBspCollisionGeometryWriter
{
    /// <summary>
    ///     Returns true only when every non-empty atomic sector has a complete,
    ///     ordered collision-flag payload and at least one usable triangle.
    /// </summary>
    internal static bool CanPopulate(RwBspWorld world)
    {
        // RwBspFile deliberately salvages individually valid atomic sectors so
        // the ordinary viewer can still show useful geometry from an imperfect
        // asset. Collision must be stricter: losing even one later sector would
        // turn that salvage into a deceptively partial collision surface.
        if (world.TotalTriangles <= 0
            || world.Sections.Sum(static section => section.Triangles.Length)
            != world.TotalTriangles)
        {
            return false;
        }

        var hasTriangle = false;
        foreach (var section in world.Sections)
        {
            if (section.Triangles.Length == 0)
                continue;
            if (section.TriangleCollisionFlags.Length != section.Triangles.Length)
                return false;

            for (var triangleIndex = 0;
                triangleIndex < section.Triangles.Length;
                 triangleIndex++)
            {
                var triangle = section.Triangles[triangleIndex];
                if (!HasValidIndices(section, triangle))
                    return false;

                if (!ModelDocumentGeometryAdapter.IsDegenerate(
                        section.Vertices[triangle.V0],
                        section.Vertices[triangle.V1],
                        section.Vertices[triangle.V2]))
                {
                    hasTriangle = true;
                }
            }
        }

        return hasTriangle;
    }

    /// <summary>
    ///     Appends collision triangles grouped by their unmodified raw flag.
    ///     Geometric degenerates are omitted, but incomplete sectors and invalid
    ///     indices make the complete source fail closed in <see cref="CanPopulate" />.
    ///     Returns zero without mutating <paramref name="document"/> when the
    ///     world's per-triangle ownership proof is incomplete.
    /// </summary>
    internal static int PopulateOverlay(ModelDocument document, RwBspWorld world)
    {
        if (!CanPopulate(world))
            return 0;

        var groups = new SortedDictionary<ushort, GeometryGroup>();
        foreach (var section in world.Sections)
        {
            for (var triangleIndex = 0;
                 triangleIndex < section.Triangles.Length;
                 triangleIndex++)
            {
                var triangle = section.Triangles[triangleIndex];
                if (!HasValidIndices(section, triangle))
                    continue;

                var flag = section.TriangleCollisionFlags[triangleIndex];
                if (!groups.TryGetValue(flag, out var group))
                {
                    group = new GeometryGroup();
                    groups.Add(flag, group);
                }

                ModelDocumentGeometryAdapter.AddTriangle(
                    group.Vertices,
                    group.Indices,
                    MakeVertex(section, triangle.V0),
                    MakeVertex(section, triangle.V1),
                    MakeVertex(section, triangle.V2));
            }
        }

        var triangleCount = groups.Values.Sum(static group => group.Indices.Count / 3);
        if (triangleCount == 0)
            return 0;

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
        foreach (var (flag, group) in groups)
        {
            var primitive = ModelDocumentGeometryAdapter.AddPrimitive(
                mesh,
                $"collision_flags_0x{flag:X4}",
                materialIndex,
                group.Vertices,
                group.Indices);
            primitive?.NativeMetadata.Add(
                new RwBspCollisionFlagsRenderMetadata(flag));
        }

        ModelDocumentGeometryAdapter.AddMeshNode(
            document, "collision_overlay", mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        return triangleCount;
    }

    private static bool HasValidIndices(RwBspSection section, RwTriangle triangle)
    {
        var count = (uint)section.Vertices.Length;
        return triangle.V0 < count
               && triangle.V1 < count
               && triangle.V2 < count;
    }

    private static ModelVertex MakeVertex(RwBspSection section, int index)
    {
        var normal = section.Normals != null && index < section.Normals.Length
            ? ModelDocumentGeometryAdapter.NormalizeOrDefault(section.Normals[index])
            : Vector3.UnitY;
        return new ModelVertex(
            section.Vertices[index], normal, Vector4.One, Vector2.Zero);
    }

    private sealed class GeometryGroup
    {
        public List<ModelVertex> Vertices { get; } = [];
        public List<int> Indices { get; } = [];
    }
}

/// <summary>Preserves the source u16 flags for one THPS3 collision primitive.</summary>
public sealed record RwBspCollisionFlagsRenderMetadata(ushort CollisionFlags)
    : NativeRenderMetadata("rw_bsp_collision_flags");
