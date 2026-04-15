using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

internal static class Ps2MdlPlacementResolver
{
    internal readonly record struct BatchPlacement(int BatchIndex, int TrailerIndex, int BoneIndex, Matrix4x4 Transform);

    internal static IReadOnlyDictionary<int, BatchPlacement> ResolveObjectPlacements(
        Ps2MdlPreamble.Preamble? preamble,
        IReadOnlyList<(int Start, int End)> batchRanges)
    {
        if (preamble?.Trailer == null || preamble.Bones.Count == 0 || batchRanges.Count == 0)
            return new Dictionary<int, BatchPlacement>();

        // Current object samples expose the trailer header/count/index array, but the render-time
        // mapping from those entries to batch indices has not been proven yet.
        //
        // Current decomp anchor:
        // - FUN_001d09e8 / FUN_001d3388 consume relocated 0x50-byte render nodes
        // - those nodes carry an explicit signed matrix index at node + 0x41
        // - the runtime applies matrix_index * 0x40 into the array at *(DAT_0049abf8 + 0x44)
        //
        // Placement stays disabled until the converter can recover that render-node matrix index
        // from the raw MDL side data deterministically.
        return new Dictionary<int, BatchPlacement>();
    }

    internal static Ps2Vertex[] ApplyPlacement(Ps2Vertex[] vertices, in BatchPlacement placement)
    {
        if (vertices.Length == 0)
            return vertices;

        var transformed = new Ps2Vertex[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            var normal = vertex.Normal;
            if (vertex.HasNormal)
            {
                normal = Vector3.TransformNormal(normal, placement.Transform);
                if (normal != Vector3.Zero)
                    normal = Vector3.Normalize(normal);
            }

            transformed[i] = new Ps2Vertex(
                Vector3.Transform(vertex.Position, placement.Transform),
                normal,
                vertex.R,
                vertex.G,
                vertex.B,
                vertex.A,
                vertex.U,
                vertex.V,
                vertex.HasNormal,
                vertex.HasColor,
                vertex.HasUV,
                vertex.IsStripRestart,
                vertex.BoneIndex0,
                vertex.BoneIndex1,
                vertex.BoneIndex2,
                vertex.BoneWeight0,
                vertex.BoneWeight1,
                vertex.BoneWeight2,
                vertex.HasSkinData);
        }

        return transformed;
    }
}
