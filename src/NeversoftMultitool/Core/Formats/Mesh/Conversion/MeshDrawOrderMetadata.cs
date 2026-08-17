namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Draw-order facts a primitive can publish for depth-tied layer stacks
///     (coplanar decals, multi-pass terrain). Consumed by the glTF exporter
///     (mesh extras neversoftDrawIndex/PassIndex/OverlapGroup — the in-app
///     viewer maps DrawIndex onto three.js renderOrder, which with the default
///     LEQUAL depth test reproduces submission-order semantics — plus the
///     BlendOffset composed into the GLB node transform, because renderOrder
///     alone only resolves the SAME polygon re-submitted: 84.5% of PSX
///     overlay pairs are DIFFERENT polygons sharing a plane, whose
///     interpolated depths disagree at ULP scale and dither under LEQUAL) and
///     by the Blender importer (object-level BlendOffset separation, since
///     EEVEE has no draw-order control for blended surfaces).
/// </summary>
public interface IMeshDrawOrderExtras
{
    int DrawIndex { get; }
    int PassIndex { get; }
    int OverlapGroup { get; }
    float BlendOffsetX { get; }
    float BlendOffsetY { get; }
    float BlendOffsetZ { get; }
}

/// <summary>
///     Generic draw-order metadata for sources whose layer stacks don't carry a
///     format-specific record (DDM decal ranks, PSX coplanar overlays). The
///     worldzone equivalent is <see cref="Ps2WorldzoneLeafRenderMetadata" />;
///     both serialize the same camelCase keys so the Blender importer and the
///     viewer treat them interchangeably. BlendOffset is the mesh-local
///     separation vector (export units) applied at OBJECT level in Blender —
///     mesh data itself stays at authored positions.
/// </summary>
public sealed record MeshDrawOrderMetadata(
    int DrawIndex,
    int PassIndex,
    int OverlapGroup,
    float BlendOffsetX = 0f,
    float BlendOffsetY = 0f,
    float BlendOffsetZ = 0f)
    : NativeRenderMetadata("draw_order"), IMeshDrawOrderExtras;

/// <summary>
///     Records that a PSX semi-transparent face's vertices were BAKED with the
///     anti-z-fighting lift (per-corner direction × Steps × 0.25 export
///     units), so a future GLB→PSX importer can subtract it and recover the
///     authored coplanar position. The opaque half of the scheme is already
///     invertible — <see cref="MeshDrawOrderMetadata" /> carries its
///     BlendOffset while the vertices stay authored — but the semi-transparent
///     half moves the vertices themselves and was previously recorded nowhere.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately does NOT implement <see cref="IMeshDrawOrderExtras" />:
///         <c>GltfModelExporter.ComposeDrawOrderSeparation</c> re-applies any
///         BlendOffset it finds into the GLB node transform, and the Blender
///         importer re-applies it again at object level — either would double
///         the already-baked lift. This record is purely informational,
///         consumed by nothing.
///     </para>
///     <para>
///         Direction is the face's own outward geometric normal — the fallback
///         the writer uses when a corner is not shared. A corner coincident
///         with other lifted faces moves along the file's POSITION-AVERAGED
///         direction instead, so one vector per face cannot name every
///         corner's exact motion. The lift is nonetheless invertible from
///         Steps alone: directions are translation-invariant face normals and
///         welded corners move together (coincident stays coincident), so the
///         averaging map rebuilt from the LIFTED geometry equals the original,
///         and subtracting direction × Steps × 0.25 recovers the authored
///         positions exactly.
///     </para>
/// </remarks>
public sealed record PsxSemiTransparentLiftMetadata(
    int Steps,
    float DirectionX,
    float DirectionY,
    float DirectionZ)
    : NativeRenderMetadata("psx_semi_transparent_lift");
