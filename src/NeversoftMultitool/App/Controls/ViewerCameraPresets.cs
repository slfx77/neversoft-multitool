namespace NeversoftMultitool;

public enum ViewerProjection
{
    Perspective,
    Orthographic,
    Isometric,
    Trimetric
}

/// <summary>
///     Camera preset constants shared by the viewer toolbar and any future
///     render-parity consumers. Values mirror BethesdaMultitool's
///     OrthoViewProjBuilder: elevation measured from horizontal (0° = side,
///     90° = top-down); Isometric/Trimetric orbit on a 45°-based azimuth grid.
/// </summary>
internal static class ViewerCameraPresets
{
    public const float OrthographicElevationDeg = 90f;
    public const float IsometricElevationDeg = 30f;
    public const float TrimetricElevationDeg = 25.65891f;

    public static string ScriptName(ViewerProjection projection)
    {
        return projection switch
        {
            ViewerProjection.Orthographic => "orthographic",
            ViewerProjection.Isometric => "isometric",
            ViewerProjection.Trimetric => "trimetric",
            _ => "perspective"
        };
    }

    /// <summary>Named views as (azimuthDeg, elevationDeg).</summary>
    public static (float AzimuthDeg, float ElevationDeg) NamedView(string name)
    {
        return name switch
        {
            "Back" => (180f, 0f),
            "Left" => (270f, 0f),
            "Right" => (90f, 0f),
            "Top" => (0f, OrthographicElevationDeg),
            _ => (0f, 0f) // Front
        };
    }
}
