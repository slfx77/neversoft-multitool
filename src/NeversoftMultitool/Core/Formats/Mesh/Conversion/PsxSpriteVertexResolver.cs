using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Static bake of PSX sprite vertices (vertex type bit4) into billboard
///     corner positions. A sprite vertex's stored fields are NOT a position:
///     raw X is the byte offset (index × 8) of the anchor vertex A in the same
///     mesh, raw Y the byte offset of the axis-mate vertex B, and Z the signed
///     perpendicular half-width in model units. At render time the engine
///     computes the corner as <c>A + k·Z·L̂</c> where L̂ is the unit
///     perpendicular of the projected A→B axis in the screen plane — an AXIAL
///     billboard rotating about A→B to face the camera, with k ≈ 1.6 for
///     vertical axes (the 512-wide framebuffer pixel-aspect bake)
///     (decomp-verified 2026-07-29, M3dAsm_TransformAndOutcodeBlockVertices
///     @0x80099728, handler 0x8009950C, byte-identical in all nine PS1-era
///     exes). This bake evaluates that formula head-on (view direction glTF
///     +Z) so the exported quad has its authored width instead of the
///     degenerate sliver the raw fields produced; corpus census:
///     tools/diagnostics/psx_sprite_vertex_census.py (5,241 sprite meshes /
///     117 files, all axes Y-dominant, all anchors on the mesh-local Y axis).
/// </summary>
internal sealed class PsxSpriteVertexResolver
{
    /// <summary>
    ///     Screen-aspect factor the engine bakes into vertical-axis expansion
    ///     (512-wide framebuffer pixels are 1.6× narrower than tall; exact
    ///     k = 1.6·|d| / |(1.6·d_x, d_y)| in camera space → 1.6 for vertical
    ///     axes, 1.0 for horizontal ones).
    /// </summary>
    internal const float VerticalAxisWidthFactor = 1.6f;

    private readonly Dictionary<uint, Vector3> _resolvedPositions;

    private PsxSpriteVertexResolver(
        Dictionary<uint, Vector3> resolvedPositions,
        PsxAxialBillboardMetadata? billboardMetadata)
    {
        _resolvedPositions = resolvedPositions;
        BillboardMetadata = billboardMetadata;
    }

    /// <summary>
    ///     Rotation-axis descriptor for the whole mesh, or null when the mesh's
    ///     sprite quads do not share a single anchor axis line (no corpus file
    ///     does that — even multi-quad sprite meshes keep every anchor on the
    ///     mesh-local Y axis — but a hypothetical divergent mesh still gets the
    ///     static bake, just no per-frame rotation).
    /// </summary>
    internal PsxAxialBillboardMetadata? BillboardMetadata { get; }

    /// <summary>
    ///     Builds the resolver for a mesh, or returns null when the mesh has no
    ///     sprite vertices (the writers then keep their normal position path).
    /// </summary>
    internal static PsxSpriteVertexResolver? TryCreate(PsxMesh mesh)
    {
        Dictionary<uint, Vector3>? resolved = null;
        List<(Vector3 Anchor, Vector3 Mate)>? axes = null;
        for (var vertexIndex = 0; vertexIndex < mesh.Vertices.Count; vertexIndex++)
        {
            var vertex = mesh.Vertices[vertexIndex];
            if (!vertex.IsSpriteVertex)
                continue;

            resolved ??= [];
            var anchorIndex = vertex.SpriteAnchorVertexIndex;
            var mateIndex = vertex.SpriteAxisMateVertexIndex;
            if ((ushort)vertex.RawX % 8 != 0 || (ushort)vertex.RawY % 8 != 0
                || anchorIndex >= mesh.Vertices.Count || mateIndex >= mesh.Vertices.Count
                || mesh.Vertices[anchorIndex].IsSpriteVertex
                || mesh.Vertices[mateIndex].IsSpriteVertex)
            {
                // Malformed reference (zero corpus hits): leave the vertex to
                // the default position path rather than inventing geometry.
                continue;
            }

            var anchor = ToGltf(mesh.Vertices[anchorIndex]);
            var mate = ToGltf(mesh.Vertices[mateIndex]);
            resolved[(uint)vertexIndex] = ResolveCorner(anchor, mate, vertex.Z);
            (axes ??= []).Add((anchor, mate));
        }

        if (resolved == null)
            return null;

        return new PsxSpriteVertexResolver(resolved, BuildBillboardMetadata(axes));
    }

    /// <summary>
    ///     Resolved billboard-corner position (glTF basis, model units) for a
    ///     sprite vertex; false for normal vertices and malformed references.
    /// </summary>
    internal bool TryResolvePosition(uint vertexIndex, out Vector3 position)
    {
        return _resolvedPositions.TryGetValue(vertexIndex, out position);
    }

    /// <summary>
    ///     The engine formula evaluated head-on: corner = A + k·Z·L̂0 with
    ///     L̂0 = normalize(cross(axisDir, +Z)) in the emitted glTF basis. The
    ///     cross flips sign with axis reversal — authored data relies on that
    ///     (corner pairs at the far axis end reference the axis reversed).
    /// </summary>
    private static Vector3 ResolveCorner(Vector3 anchor, Vector3 mate, float halfWidth)
    {
        var axis = mate - anchor;
        var axisLengthSquared = axis.LengthSquared();
        var perpendicular = Vector3.UnitX;
        var widthFactor = 1f;
        if (axisLengthSquared > 1e-12f)
        {
            var axisDirection = axis / MathF.Sqrt(axisLengthSquared);
            widthFactor = IsVerticalDominant(axisDirection) ? VerticalAxisWidthFactor : 1f;
            var cross = Vector3.Cross(axisDirection, Vector3.UnitZ);
            var crossLengthSquared = cross.LengthSquared();
            if (crossLengthSquared > 1e-12f)
                perpendicular = cross / MathF.Sqrt(crossLengthSquared);
        }

        return anchor + widthFactor * halfWidth * perpendicular;
    }

    private static bool IsVerticalDominant(Vector3 axisDirection)
    {
        var absY = MathF.Abs(axisDirection.Y);
        return absY >= MathF.Abs(axisDirection.X) && absY >= MathF.Abs(axisDirection.Z);
    }

    /// <summary>
    ///     One shared rotation axis for the mesh: every corner's anchor and
    ///     mate must sit on a single line (within a 1/64-model-unit tolerance,
    ///     far under the ~0.44 authoring grid step). The published anchor is
    ///     the midpoint of the endpoints' axis-projection extent and the axis
    ///     direction is sign-canonicalized so coincident up/down authoring
    ///     (corpus quads reference the axis both ways) yields one descriptor.
    /// </summary>
    private static PsxAxialBillboardMetadata? BuildBillboardMetadata(
        List<(Vector3 Anchor, Vector3 Mate)>? axes)
    {
        if (axes == null || axes.Count == 0)
            return null;

        var direction = Vector3.Zero;
        foreach (var (anchor, mate) in axes)
        {
            var axis = mate - anchor;
            if (axis.LengthSquared() <= 1e-12f)
                continue;

            var normalized = Vector3.Normalize(axis);
            direction = Canonicalize(normalized);
            break;
        }

        if (direction == Vector3.Zero)
            return null;

        var origin = axes[0].Anchor;
        var minProjection = float.PositiveInfinity;
        var maxProjection = float.NegativeInfinity;
        const float toleranceSquared = (1f / 64f) * (1f / 64f);
        foreach (var (anchor, mate) in axes)
        {
            foreach (var point in (ReadOnlySpan<Vector3>)[anchor, mate])
            {
                var offset = point - origin;
                var projection = Vector3.Dot(offset, direction);
                if ((offset - projection * direction).LengthSquared() > toleranceSquared)
                    return null;

                minProjection = MathF.Min(minProjection, projection);
                maxProjection = MathF.Max(maxProjection, projection);
            }
        }

        var midpoint = origin + (minProjection + maxProjection) * 0.5f * direction;
        return new PsxAxialBillboardMetadata(
            midpoint.X, midpoint.Y, midpoint.Z,
            direction.X, direction.Y, direction.Z);
    }

    /// <summary>Flips the direction so its dominant component is positive.</summary>
    private static Vector3 Canonicalize(Vector3 direction)
    {
        var absX = MathF.Abs(direction.X);
        var absY = MathF.Abs(direction.Y);
        var absZ = MathF.Abs(direction.Z);
        float dominant;
        if (absY >= absX && absY >= absZ)
            dominant = direction.Y;
        else if (absX >= absZ)
            dominant = direction.X;
        else
            dominant = direction.Z;
        return dominant < 0f ? -direction : direction;
    }

    private static Vector3 ToGltf(PsxVertex vertex)
    {
        return new Vector3(vertex.X, -vertex.Y, -vertex.Z);
    }
}
