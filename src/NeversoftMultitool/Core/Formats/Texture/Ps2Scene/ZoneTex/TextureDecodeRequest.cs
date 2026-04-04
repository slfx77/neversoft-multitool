namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

internal readonly record struct TextureDecodeRequest(
    ulong Tex0,
    uint Checksum,
    uint Tbp,
    uint Cbp,
    int Width,
    int Height,
    uint Psm,
    uint Cpsm,
    bool NeedsPalette,
    bool HasExactTbpUpload,
    bool HasExactCbpUpload,
    int? TargetUploadIndex);
