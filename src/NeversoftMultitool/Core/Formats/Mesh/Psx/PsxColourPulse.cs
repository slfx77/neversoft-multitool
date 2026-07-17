namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     A looping PS1 RGB palette animation from tagged chunk 7. Its serialized
///     pre-tick playhead is baked into the parsed Gouraud palette for static
///     export while all keys remain available for animation-aware consumers.
/// </summary>
public sealed class PsxColourPulse
{
    public required byte ColourIndex { get; init; }
    public required byte InitialKeyIndex { get; init; }
    public required byte InitialTimeAccumulator { get; init; }
    public required PsxColourPulseKey[] Keys { get; init; }
}

public readonly record struct PsxColourPulseKey(
    byte R,
    byte G,
    byte B,
    byte Interval);
