using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     The engine's world → isometric-art transform, and the per-level origin it
///     needs.
/// </summary>
/// <remarks>
///     <code>
///     artX = X0 + 16·(wy − wx)
///     artY = Y0 +  8·(wx + wy) − 16·z      (world units; a collision cell = 3 units)
///     </code>
///     The 16/8/−16 pixels-per-world-unit constants are engine-wide; the origin
///     <c>(X0, Y0)</c> is stored per level in the record at <c>+0x64/+0x68</c> as
///     signed 24.8 fixed (every level decodes to whole pixels). Established
///     dynamically — skater world coordinates captured at the engine's collision
///     query (0x08023168) chained to the shadow sprite's OAM screen position across
///     three attract-demo levels, median residual ~1 px — then the origin was found
///     as a ROM field, making the whole transform media-derived.
///     <para>
///         Caveats that survive: the <c>−16</c> was measured over ground z 0–10.5, so
///         kill-wall heights extrapolate; six of the nine levels' origins are ROM-read
///         but not demo-visited; and the <c>+0x64/+0x68</c> identification is numeric
///         (three independently fitted origins matched the fields), not traced through
///         the loader's <c>ldr</c> instructions.
///     </para>
///     <para>
///         This exists because the transform had been written out three times — the
///         collision overlay, the 3D writer's UVs, and the research notes — and a
///         constant that appears three times is one that can disagree with itself.
///     </para>
/// </remarks>
public static class GbaLevelArtProjection
{
    /// <summary>Art pixels per world unit along the isometric x axis.</summary>
    public const double PixelsPerUnitX = 16.0;

    /// <summary>Art pixels per world unit along the isometric y axis.</summary>
    public const double PixelsPerUnitY = 8.0;

    /// <summary>Art pixels per world unit of height. Negative: up on screen.</summary>
    public const double PixelsPerUnitZ = -16.0;

    /// <summary>World units spanned by one collision cell.</summary>
    public const double WorldUnitsPerCell = 3.0;

    private const int OriginField = 0x64;

    /// <summary>
    ///     The level's art origin in whole pixels, or null when the record does not
    ///     reach far enough to carry one.
    /// </summary>
    public static (double X, double Y)? TryReadOrigin(ReadOnlySpan<byte> rom, int trueRecordOffset)
    {
        if (trueRecordOffset < 0 || trueRecordOffset + OriginField + 8 > rom.Length)
            return null;

        var x = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(trueRecordOffset + OriginField, 4));
        var y = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(trueRecordOffset + OriginField + 4, 4));
        return (x / 256.0, y / 256.0);
    }

    /// <summary>Project a world point onto the level's art.</summary>
    public static (double X, double Y) Project(
        (double X, double Y) origin, double worldX, double worldY, double worldZ) =>
        (origin.X + PixelsPerUnitX * (worldY - worldX),
            origin.Y + PixelsPerUnitY * (worldX + worldY) + PixelsPerUnitZ * worldZ);

    /// <summary>
    ///     The world position of a sub-sample within a collision cell, where
    ///     <paramref name="i" /> and <paramref name="j" /> run 0..<paramref name="subDivisions" />.
    /// </summary>
    public static (double X, double Y) CellSamplePosition(
        int gridX, int gridY, int i, int j, int subDivisions) =>
        ((gridX + (double)i / subDivisions) * WorldUnitsPerCell,
            (gridY + (double)j / subDivisions) * WorldUnitsPerCell);

    /// <summary>
    ///     How much art area one unit of world area covers, given the surface's
    ///     slope. Flat ground yields <c>-256</c>.
    /// </summary>
    /// <remarks>
    ///     This is the Jacobian determinant of <see cref="Project" /> with respect to
    ///     world <c>(x, y)</c>, taking <c>z = h(x, y)</c>:
    ///     <code>
    ///     du/dx = -16      du/dy = +16
    ///     dv/dx = 8 - 16·hx    dv/dy = 8 - 16·hy
    ///     det   = 256·(hx + hy - 1)
    ///     </code>
    ///     It is <b>zero</b> where the surface rises at 45° in the combined view
    ///     direction, and changes sign past that: the engine's single baked view
    ///     folds. A surface at that slope projects to no art area at all, so no
    ///     texture mapping can be faithful there — which is what "the diagonals look
    ///     wavy" is. It is a property of the projection, not of any approximation
    ///     this tool makes.
    /// </remarks>
    public static double ProjectedAreaPerWorldArea(double slopeX, double slopeY) =>
        256.0 * (slopeX + slopeY - 1.0);

    /// <summary>
    ///     Whether a surface of this slope is at or past the fold, where the art
    ///     carries no usable detail.
    /// </summary>
    /// <remarks>
    ///     Threshold-free on purpose: flat ground gives a negative determinant, so
    ///     "the sign has flipped" IS the criterion. Measured over all nine THPS2
    ///     levels, quads on the near side average well under one art pixel of
    ///     mapping error, while quads past it average 5-17 px and reach 268.
    /// </remarks>
    public static bool IsGrazing(double slopeX, double slopeY) =>
        ProjectedAreaPerWorldArea(slopeX, slopeY) >= 0.0;
}
