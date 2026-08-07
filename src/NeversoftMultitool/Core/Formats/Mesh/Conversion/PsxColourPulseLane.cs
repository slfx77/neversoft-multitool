namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     The single definition of how a colour-pulse channel is packed into
///     <c>_PSX_FLAGS_0.Y</c>, so the exporter, the viewer and the tests cannot
///     drift apart on it.
///     <para>
///         Y already means "this corner is Gouraud shaded" and is read as
///         <c>y &gt;= 0.5</c>. Unpulsed Gouraud corners keep Y = 1. A pulsed
///         corner stores <c>1 + oneBasedChannel</c>, so the smallest pulsed
///         value is 2 and never collides with the plain Gouraud flag. The table
///         index is therefore <c>Y - 2</c> — NOT <c>Y - 1</c>, because the
///         channel is already 1-based.
///     </para>
///     <para>
///         RULE: this lane is CPU-side data. Y is a varying, and a triangle that
///         mixes pulsed and unpulsed corners interpolates it to meaningless
///         intermediate values, so no shader may read it.
///     </para>
/// </summary>
public static class PsxColourPulseLane
{
    /// <summary>Y below this is an unpulsed corner (0 = flat, 1 = plain Gouraud).</summary>
    public const float PulsedThreshold = 1.5f;

    /// <summary>Packs a 1-based channel into the Gouraud lane.</summary>
    public static float Encode(int oneBasedChannel)
    {
        return oneBasedChannel > 0 ? 1f + oneBasedChannel : 1f;
    }

    /// <summary>
    ///     Recovers the zero-based channel table index, or -1 when the corner is
    ///     not pulsed.
    /// </summary>
    public static int DecodeIndex(float laneValue)
    {
        return laneValue < PulsedThreshold ? -1 : (int)MathF.Round(laneValue) - 2;
    }
}
