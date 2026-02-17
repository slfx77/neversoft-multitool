namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
/// Holds a complete mip chain for a PowerVR texture.
/// Levels are ordered from largest (main surface) to smallest (1x1), matching DDS convention.
/// </summary>
public sealed class PvrMipChain
{
    /// <summary>
    /// Mip levels ordered largest to smallest. Index 0 is the main surface.
    /// Each level is a flat ushort[] of width * height pixels for that level.
    /// </summary>
    public required List<ushort[]> Levels { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    public int MipCount => Levels.Count;
    public ushort[] MainSurface => Levels[0];
}
