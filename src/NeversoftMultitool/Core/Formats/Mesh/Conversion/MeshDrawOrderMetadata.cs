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
