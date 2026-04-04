namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

internal readonly record struct HeaderClutEntropyContext(
    ThawZoneTexFile.ZoneTexHeaderEntry Entry,
    int Bias,
    double ClutEntropy);
