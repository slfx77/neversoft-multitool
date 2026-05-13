namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record XbxMaterialRenderMetadata(
    uint Checksum,
    uint NameChecksum,
    int AlphaCutoff,
    bool Sorted,
    float DrawOrder,
    int ZBias,
    uint? FirstTextureChecksum)
    : NativeRenderMetadata("xbx_material");
