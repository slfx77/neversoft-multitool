namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record DdmBlendRenderMetadata(
    uint BlendMode,
    uint DrawOrder,
    string TextureName,
    byte DiffuseR,
    byte DiffuseG,
    byte DiffuseB,
    byte DiffuseA)
    : NativeRenderMetadata("ddm_blend");
