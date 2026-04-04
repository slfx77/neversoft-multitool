namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

internal readonly record struct HeaderDataSlotCandidate(
    Ps2Texture Texture,
    double Score,
    int Bias);
