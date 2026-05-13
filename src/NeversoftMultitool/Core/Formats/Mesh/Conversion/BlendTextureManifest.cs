namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendTextureManifest
{
    public required string Name { get; init; }
    public string? PngPath { get; init; }
    public string? RgbaPath { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public required string WrapU { get; init; }
    public required string WrapV { get; init; }
    public uint? NativeChecksum { get; init; }
}
