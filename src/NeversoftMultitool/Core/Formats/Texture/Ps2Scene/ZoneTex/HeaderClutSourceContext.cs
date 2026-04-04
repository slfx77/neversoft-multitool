namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

internal readonly record struct HeaderClutSourceContext(
    ThawZoneTexFile.ZoneTexHeaderEntry Entry,
    int Bias);
