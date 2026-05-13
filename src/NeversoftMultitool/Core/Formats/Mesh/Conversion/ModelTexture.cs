namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelTexture
{
    public required string Name { get; init; }
    public byte[]? PngBytes { get; init; }
    public ModelTextureWrap WrapU { get; init; } = ModelTextureWrap.Repeat;
    public ModelTextureWrap WrapV { get; init; } = ModelTextureWrap.Repeat;
    public uint? NativeChecksum { get; init; }
}
