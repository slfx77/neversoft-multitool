namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Per-primitive descriptor for a THAW worldzone Format-B billboard. Emitted alongside
///     <see cref="Ps2WorldzoneLeafRenderMetadata" /> when the leaf is a billboard. The Blender
///     importer reads this to attach a Track-To constraint that orients the quad toward the
///     active camera at render time; glTF consumers ignore it and use the static fallback
///     geometry instead. Coordinates are in PS2 source-space; the importer applies the
///     Y_UP_TO_Z_UP transform at scene load.
///     <c>BillboardKind</c> is the variant name ("ScreenAligned" / "LongAxis" / "ShortAxis"),
///     distinct from the base <c>Kind</c> field which discriminates metadata types.
/// </summary>
public sealed record Ps2WorldzoneBillboardMetadata(
    string BillboardKind,
    float AnchorX,
    float AnchorY,
    float AnchorZ,
    float Width,
    float Height,
    float PivotX,
    float PivotY,
    float PivotZ,
    float AxisX,
    float AxisY,
    float AxisZ)
    : NativeRenderMetadata("ps2_worldzone_billboard");
