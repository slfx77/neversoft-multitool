namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Per-leaf draw metadata for THAW PS2 worldzone geometry. RenderOrder is the
///     coarse engine sort key (GetWorldzoneRenderOrderKey); DrawIndex is the
///     leaf's global rank in the converter's draw order; PassIndex is its rank
///     within the set of leaves sharing one geometry key (the multi-pass terrain
///     pattern — pass 0 is the base layer); OverlapGroup identifies that set.
///     BlendOffset is the mesh-local separation vector (already in export units)
///     that the Blender importer applies at OBJECT level so coplanar passes don't
///     z-fight in EEVEE — mesh data itself stays at authored positions.
/// </summary>
public sealed record Ps2WorldzoneLeafRenderMetadata(
    string MdlName,
    int LeafIndex,
    string Space,
    string RenderLayer,
    uint RenderOrder,
    bool IsBillboard,
    bool IsLocalSpace,
    uint NodeColour = 0x80808080,
    uint NodeFlags = 0,
    int DrawIndex = -1,
    int PassIndex = 0,
    int OverlapGroup = -1,
    float BlendOffsetX = 0f,
    float BlendOffsetY = 0f,
    float BlendOffsetZ = 0f)
    : NativeRenderMetadata("ps2_worldzone_leaf"), IMeshDrawOrderExtras;
