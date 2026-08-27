namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendAnimationManifest
{
    public required string Name { get; init; }
    public required List<BlendAnimationChannelManifest> Channels { get; init; }

    /// <summary>Null unless the animation drives morph weights; an animation may
    ///     carry only this and no bone channels (the GBA skater has no skeleton).</summary>
    public BlendMorphChannelManifest? MorphChannel { get; init; }
}
