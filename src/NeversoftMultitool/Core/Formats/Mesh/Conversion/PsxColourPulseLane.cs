namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Compatibility-facing definition of the colour-pulse code stored in
///     normalized <c>COLOR_1.W</c>. Zero means static; values 1..255 are exact
///     byte code points and identify 1-based channels.
/// </summary>
public static class PsxColourPulseLane
{
    public const float PulsedThreshold = 0.5f / byte.MaxValue;

    /// <summary>Packs a 1-based channel into normalized COLOR_1 alpha.</summary>
    public static float Encode(int oneBasedChannel)
    {
        if ((uint)oneBasedChannel > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(oneBasedChannel));
        return oneBasedChannel / (float)byte.MaxValue;
    }

    /// <summary>
    ///     Recovers the zero-based channel table index, or -1 when the corner is
    ///     not pulsed.
    /// </summary>
    public static int DecodeIndex(float laneValue)
    {
        return laneValue < PulsedThreshold
            ? -1
            : Math.Clamp((int)MathF.Round(laneValue * byte.MaxValue), 1, byte.MaxValue) - 1;
    }
}
