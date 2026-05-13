namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record Ps2GsRenderMetadata(
    ulong? Alpha,
    ulong? Test,
    ulong? Tex0,
    ulong? Tex1,
    ulong? Texa,
    ulong? Clamp,
    uint? TextureChecksum,
    uint? GroupChecksum,
    int? AlphaRef,
    string? Source,
    ulong? Frame = null)
    : NativeRenderMetadata("ps2_gs");
