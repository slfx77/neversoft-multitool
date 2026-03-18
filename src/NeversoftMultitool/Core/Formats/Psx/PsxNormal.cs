namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     A normal vector in a PSX mesh. Pre-divided by 4096.
/// </summary>
public sealed class PsxNormal
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}
