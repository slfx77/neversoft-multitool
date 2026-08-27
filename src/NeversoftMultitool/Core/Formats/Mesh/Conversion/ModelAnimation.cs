namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelAnimation
{
    public required string Name { get; init; }

    /// <summary>Bone TRS tracks. Empty for a clip that animates only by morphing.</summary>
    public List<ModelAnimationChannel> Channels { get; } = [];

    /// <summary>
    ///     Optional morph-weight track, for formats that animate by blending
    ///     complete posed vertex sets rather than by transforming a skeleton.
    /// </summary>
    public ModelMorphChannel? MorphChannel { get; init; }

    public bool IsEmpty => Channels.Count == 0 && MorphChannel == null;
}
