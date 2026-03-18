using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexFile;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal readonly record struct HeaderDataSlotCandidate(
    Ps2Texture Texture,
    double Score,
    int Bias);

internal readonly record struct HeaderClutSourceContext(
    ZoneTexHeaderEntry Entry,
    int Bias);

internal readonly record struct HeaderClutEntropyContext(
    ZoneTexHeaderEntry Entry,
    int Bias,
    double ClutEntropy);

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
