namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed record GsResolvedTexture(int Width, int Height, byte[] Rgba, uint? Checksum = null);
