namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record RwGsAlphaRenderMetadata(
    byte GsAlpha,
    byte GsAlphaFix,
    bool IsAdditive,
    bool IsSubtractive,
    bool IsBlend,
    string? TextureName)
    : NativeRenderMetadata("rw_gs_alpha");
