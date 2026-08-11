namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Portable, Blender-facing PS1 colour-pulse channel. Packet-domain keys
///     deliberately stay out of the direct Blend package: Blender materials
///     consume the same linear <c>Color</c> attribute as the portable glTF path.
/// </summary>
internal sealed class BlendColourPulseChannelManifest
{
    public required List<float[]> PortableKeys { get; init; }
    public required List<int> Intervals { get; init; }
    public byte InitialKeyIndex { get; init; }
    public byte InitialAccumulator { get; init; }
}
