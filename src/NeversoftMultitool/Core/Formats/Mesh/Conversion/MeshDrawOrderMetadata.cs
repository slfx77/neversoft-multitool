namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Draw-order facts a primitive can publish for depth-tied layer stacks
///     (coplanar decals, multi-pass terrain). Consumed by the glTF exporter
///     (mesh extras neversoftDrawIndex/PassIndex/OverlapGroup — the in-app
///     viewer maps DrawIndex onto three.js renderOrder, which with the default
///     LEQUAL depth test reproduces submission-order semantics) and by the
///     Blender importer (object-level BlendOffset separation, since EEVEE has
///     no draw-order control for blended surfaces).
/// </summary>
public interface IMeshDrawOrderExtras
{
    int DrawIndex { get; }
    int PassIndex { get; }
    int OverlapGroup { get; }
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
