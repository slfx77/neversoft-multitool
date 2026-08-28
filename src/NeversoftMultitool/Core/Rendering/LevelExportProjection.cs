namespace NeversoftMultitool.Core.Rendering;

/// <summary>How a level render frames the world.</summary>
public enum LevelExportProjection
{
    /// <summary>Straight down. Reads as a floor plan.</summary>
    Orthographic,

    /// <summary>Equal foreshortening on all three axes — the classic game view.</summary>
    Isometric,

    /// <summary>Unequal foreshortening: shallower, so walls stay readable.</summary>
    Trimetric
}

/// <summary>One named camera direction and the angles that produce it.</summary>
public readonly record struct LevelExportView(string Label, string StemSuffix, float Azimuth, float Elevation);

/// <summary>
///     The projection and direction choices a level export offers.
/// </summary>
/// <remarks>
///     Modelled on Bethesda Multitool's world export, which offers projection plus a
///     compass direction rather than raw angles: a level has no natural "front", so
///     "from the north-east" is answerable where "azimuth -90" is not.
///     <para>
///         A straight-down view flattens every wall to nothing, which makes one
///         building indistinguishable from the next — hence isometric is the default,
///         as it is there.
///     </para>
/// </remarks>
public static class LevelExportProjections
{
    /// <summary>
    ///     Trimetric's elevation. Bethesda Multitool's own constant, kept so the two
    ///     tools' trimetric shots are the same shot.
    /// </summary>
    public const float TrimetricElevation = 25.65891f;

    /// <summary>Isometric elevation.</summary>
    public const float IsometricElevation = 30f;

    /// <summary>Straight down.</summary>
    public const float OrthographicElevation = 90f;

    private static readonly LevelExportView[] TopDown =
    [
        new("North at top", "_n", 0f, OrthographicElevation),
        new("East at top", "_e", 90f, OrthographicElevation),
        new("South at top", "_s", 180f, OrthographicElevation),
        new("West at top", "_w", 270f, OrthographicElevation)
    ];

    private static readonly LevelExportView[] Isometric =
    [
        new("From the NE", "_ne", 45f, IsometricElevation),
        new("From the NW", "_nw", 135f, IsometricElevation),
        new("From the SW", "_sw", 225f, IsometricElevation),
        new("From the SE", "_se", 315f, IsometricElevation)
    ];

    private static readonly LevelExportView[] Trimetric =
    [
        new("From the NE", "_tri_ne", 30f, TrimetricElevation),
        new("From the NW", "_tri_nw", 120f, TrimetricElevation),
        new("From the SW", "_tri_sw", 210f, TrimetricElevation),
        new("From the SE", "_tri_se", 300f, TrimetricElevation)
    ];

    /// <summary>The directions available under one projection, in display order.</summary>
    public static IReadOnlyList<LevelExportView> Directions(LevelExportProjection projection) =>
        projection switch
        {
            LevelExportProjection.Orthographic => TopDown,
            LevelExportProjection.Trimetric => Trimetric,
            _ => Isometric
        };

    /// <summary>A label for the projection itself.</summary>
    public static string Label(LevelExportProjection projection) => projection switch
    {
        LevelExportProjection.Orthographic => "Orthographic (top-down)",
        LevelExportProjection.Trimetric => "Trimetric",
        _ => "Isometric"
    };

    /// <summary>
    ///     The largest render this may produce. The software rasterizer supersamples
    ///     2x, so a 4096 long edge already allocates on the order of 360 MB.
    /// </summary>
    public const int MaxLongEdge = 4096;
}
