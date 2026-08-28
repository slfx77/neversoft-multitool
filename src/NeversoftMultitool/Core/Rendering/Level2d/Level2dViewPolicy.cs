namespace NeversoftMultitool.Core.Rendering.Level2d;

/// <summary>
///     Whether a level can be shown as a picture, and whether it should be by
///     default.
/// </summary>
public static class Level2dViewPolicy
{
    /// <summary>Whether this level has an authored picture to show at all.</summary>
    public static bool Supports(string fileName) => GbaLevel2dSource.Supports(fileName);

    /// <summary>
    ///     Whether the 2D view is the one to open on.
    /// </summary>
    /// <remarks>
    ///     2D is the default exactly where the level WAS authored as a picture — the
    ///     3D surface is then the derived view, not the original. Every other format
    ///     is geometry first and has no picture to prefer, so it opens in 3D.
    /// </remarks>
    public static bool DefaultsToTwoDimensional(string fileName) => Supports(fileName);

    /// <summary>A short label for a layer, for the picker and for export stems.</summary>
    public static string LayerLabel(Level2dLayer layer) => layer switch
    {
        Level2dLayer.Art => "Artwork",
        Level2dLayer.CollisionHeightfield => "Collision",
        Level2dLayer.CollisionOverArt => "Collision over artwork",
        _ => layer.ToString()
    };

    /// <summary>The suffix a layer's exported PNG carries, so the file says which it is.</summary>
    public static string LayerStemSuffix(Level2dLayer layer) => layer switch
    {
        Level2dLayer.Art => "",
        Level2dLayer.CollisionHeightfield => "_collision",
        Level2dLayer.CollisionOverArt => "_overlay",
        _ => "_" + layer.ToString().ToLowerInvariant()
    };
}
