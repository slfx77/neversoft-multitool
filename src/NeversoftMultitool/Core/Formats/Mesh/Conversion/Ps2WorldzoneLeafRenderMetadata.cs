namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record Ps2WorldzoneLeafRenderMetadata(
    string MdlName,
    int LeafIndex,
    string Space,
    string RenderLayer,
    uint RenderOrder,
    bool IsBillboard,
    bool IsLocalSpace,
    uint NodeColour = 0x80808080,
    uint NodeFlags = 0)
    : NativeRenderMetadata("ps2_worldzone_leaf");
