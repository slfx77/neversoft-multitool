namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Per-primitive descriptor for a PSX sprite-vertex axial billboard: a quad
///     anchored on the world-space axis A→B that rotates about that axis to
///     face the camera (vertex type bit4; decomp-verified 2026-07-29,
///     M3dAsm_TransformAndOutcodeBlockVertices @0x80099728 handler 0x8009950C).
///     <see cref="PsxSpriteVertexResolver" /> bakes the static head-on facing
///     into the exported positions; this record carries the rotation axis so
///     live consumers can re-face the quad per frame. The glTF exporter
///     publishes it as mesh extras <c>neversoftAxialBillboard</c> /
///     <c>neversoftBillboardAxis</c> / <c>neversoftBillboardAnchor</c> (the
///     in-app viewer spins tagged meshes about the axis toward the camera);
///     the Blender importer attaches a Locked Track constraint with the axis
///     locked. Anchor and axis are in mesh-local glTF units — corpus-wide
///     (5,241 sprite meshes across 117 files, psx_sprite_vertex_census.py)
///     every anchor sits exactly on the mesh-local Y axis, so the axis line
///     always passes through the node origin.
/// </summary>
public sealed record PsxAxialBillboardMetadata(
    float AnchorX,
    float AnchorY,
    float AnchorZ,
    float AxisX,
    float AxisY,
    float AxisZ)
    : NativeRenderMetadata("psx_axial_billboard");
