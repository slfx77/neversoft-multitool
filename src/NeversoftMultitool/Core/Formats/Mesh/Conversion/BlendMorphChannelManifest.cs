namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     A morph-weight track. Weights are laid out key-major
///     (<c>KeyCount</c> × <c>TargetCount</c> float32) and apply mesh-wide, so
///     every Blender object built from <see cref="MeshIndex" /> takes the same
///     values on its own shape-key blocks.
/// </summary>
internal sealed class BlendMorphChannelManifest
{
    public required int MeshIndex { get; init; }
    public required int TargetCount { get; init; }
    public required string TimesBuffer { get; init; }
    public required string WeightsBuffer { get; init; }
    public required int KeyCount { get; init; }
}
