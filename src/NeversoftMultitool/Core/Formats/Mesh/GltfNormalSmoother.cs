using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     Post-processes a glTF <see cref="ModelRoot" /> to smooth normals at coincident
///     vertex positions. Many Neversoft mesh exporters emit split vertices (unique per face)
///     because UV/color attributes differ, which prevents SharpGLTF from merging them.
///     This pass averages normals across all vertices that share the same world-space
///     position so that lighting interpolates smoothly across triangle boundaries.
/// </summary>
internal static class GltfNormalSmoother
{
    /// <summary>
    ///     Smooth normals in-place on a built <see cref="ModelRoot" />.
    ///     Call this after <c>SceneBuilder.ToGltf2()</c> and before <c>SaveGLB()</c>.
    /// </summary>
    public static void SmoothNormals(ModelRoot model)
    {
        foreach (var mesh in model.LogicalMeshes)
        {
            foreach (var prim in mesh.Primitives)
            {
                var posAccessor = prim.GetVertexAccessor("POSITION");
                var nrmAccessor = prim.GetVertexAccessor("NORMAL");
                if (posAccessor == null || nrmAccessor == null) continue;

                var positions = posAccessor.AsVector3Array();
                var normals = nrmAccessor.AsVector3Array();
                var count = positions.Count;
                if (count == 0) continue;

                // Build map: quantized position → list of vertex indices
                var posMap = new Dictionary<(int, int, int), List<int>>();
                for (var i = 0; i < count; i++)
                {
                    var p = positions[i];
                    var key = (
                        (int)MathF.Round(p.X * 1024f),
                        (int)MathF.Round(p.Y * 1024f),
                        (int)MathF.Round(p.Z * 1024f)
                    );

                    ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(posMap, key, out var exists);
                    if (!exists) list = [];
                    list!.Add(i);
                }

                // Average normals at shared positions and write back
                var modified = false;
                foreach (var group in posMap.Values)
                {
                    if (group.Count <= 1) continue;

                    var sum = Vector3.Zero;
                    foreach (var idx in group)
                        sum += normals[idx];

                    var len = sum.Length();
                    if (len > 0.001f) sum /= len;

                    foreach (var idx in group)
                        normals[idx] = sum;

                    modified = true;
                }

                // Update accessor bounds after modification to avoid validation errors
                if (modified)
                    nrmAccessor.UpdateBounds();
            }
        }
    }
}
