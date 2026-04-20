using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

internal static class Ps2MdlPlacementResolver
{
    /// <summary>
    ///     PS2→glTF axis swap: Y↔Z with PS2 Y becoming glTF -Z. Matches the root bone's
    ///     transform in THAW worldzone object MDLs. Used for level MDLs without a bone
    ///     hierarchy so they land in the same coordinate system as object MDLs.
    /// </summary>
    internal static readonly Quaternion Ps2ToGltfAxisSwap =
        Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            1, 0, 0, 0,
            0, 0, -1, 0,
            0, 1, 0, 0,
            0, 0, 0, 1));

    internal readonly record struct BatchPlacement(int BatchIndex, int TrailerIndex, int BoneIndex, Matrix4x4 Transform);

    /// <summary>
    ///     Per-block worldzone placement derived from pairing a .91E1028D record with the
    ///     preceding .mdl's preamble records. Used by the worldzone CLI flow to spawn one scene
    ///     node per placed object.
    /// </summary>
    internal readonly record struct WorldzonePlacement(
        int BlockIndex,
        uint ClassHash,
        Vector3 Position,
        Quaternion Rotation);

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

    /// <summary>
    ///     Resolve worldzone placements from CHierarchyObject bone matrices in the MDL preamble.
    ///     Returns one placement per non-root bone (for LOCAL-space batches that should be
    ///     instanced per bone) PLUS the root axis-swap at [0] (for WORLD-space batches).
    ///     Callers must split output by <see cref="Ps2GeomLeaf.IsLocalSpace"/>: pass [0] only
    ///     to world leaves and [1..] to local leaves. This matches the THAW worldzone object
    ///     MDL layout where car-shaped batches are in bone-local space and shared
    ///     infrastructure is in world space.
    /// </summary>
    internal static IReadOnlyList<WorldzonePlacement> ResolveWorldzonePlacements(
        Ps2MdlPreamble.Preamble preamble)
    {
        ArgumentNullException.ThrowIfNull(preamble);

        if (preamble.Bones.Count == 0)
            return [];

        var rootIdx = -1;
        for (var i = 0; i < preamble.Bones.Count; i++)
        {
            if (preamble.Bones[i].ParentChecksum != 0)
                continue;
            rootIdx = i;
            break;
        }
        if (rootIdx < 0)
            return [];

        var root = preamble.Bones[rootIdx];
        var rootRotation = Quaternion.CreateFromRotationMatrix(root.Transform);

        var placements = new List<WorldzonePlacement>(preamble.Bones.Count)
        {
            // [0] = root axis-swap for world-space leaves.
            new(0, root.Checksum, Vector3.Zero, rootRotation)
        };

        // [1..] = one placement per non-root bone for local-space leaves. Bake the root
        // PS2→glTF axis swap into each bone's transform.
        for (var i = 0; i < preamble.Bones.Count; i++)
        {
            if (i == rootIdx)
                continue;

            var bone = preamble.Bones[i];
            var combined = bone.Transform * root.Transform;
            if (!Matrix4x4.Decompose(combined, out _, out var rotation, out var position))
            {
                position = new Vector3(combined.M41, combined.M42, combined.M43);
                rotation = Quaternion.CreateFromRotationMatrix(combined);
            }
            placements.Add(new WorldzonePlacement(
                BlockIndex: i,
                ClassHash: bone.Checksum,
                Position: position,
                Rotation: rotation));
        }

        return placements;
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
