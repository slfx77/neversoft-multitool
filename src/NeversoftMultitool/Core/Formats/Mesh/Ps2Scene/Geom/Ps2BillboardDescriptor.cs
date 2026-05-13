using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     PS2 Format-B billboard parametric descriptor. Decoded from the four V4_32
///     "positions" consumed by ScreenAlignedBillboards / LongAxisBillboards /
///     ShortAxisBillboards in vu1code.dsm:
///     <list type="bullet">
///         <item><c>Anchor</c> = pvw, world pivot position.</item>
///         <item><c>Size</c>   = (width, height).</item>
///         <item>
///             <c>PivotLocal</c> = pvl, pivot-local 3-vec offset (subtracted in the
///             udir/vdir/wdir basis built by the VU1 microcode).
///         </item>
///         <item>
///             <c>Axis</c>   = world axis for axis-aligned variants. Zero for the
///             screen-aligned variant.
///         </item>
///     </list>
///     On z_sm all 145 Format-B leaves are axis-aligned with <c>Axis = (0, 1, 0)</c>;
///     <see cref="Kind" /> is computed from <c>|Axis|</c>.
/// </summary>
public readonly record struct Ps2BillboardDescriptor(
    Vector3 Anchor,
    Vector2 Size,
    Vector3 PivotLocal,
    Vector3 Axis,
    Ps2BillboardKind Kind);
