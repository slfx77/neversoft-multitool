namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelTexture
{
    public required string Name { get; init; }
    public byte[]? PngBytes { get; init; }
    public ModelTextureWrap WrapU { get; init; } = ModelTextureWrap.Repeat;
    public ModelTextureWrap WrapV { get; init; } = ModelTextureWrap.Repeat;
    public uint? NativeChecksum { get; init; }

    /// <summary>
    ///     Emit nearest (unfiltered) samplers. For consoles that have no texture
    ///     filtering at all — the DS samples floor(texel), nothing else — linear
    ///     defaults both soften the art and bleed neighbouring atlas islands
    ///     across UV borders as visible seams.
    /// </summary>
    public bool NearestFilter { get; init; }
}
