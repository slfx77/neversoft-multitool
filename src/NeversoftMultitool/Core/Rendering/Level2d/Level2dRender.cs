namespace NeversoftMultitool.Core.Rendering.Level2d;

/// <summary>Which picture of a level a 2D view is showing.</summary>
public enum Level2dLayer
{
    /// <summary>The authored artwork, as the game shipped it. No collision, no mesh.</summary>
    Art,

    /// <summary>The collision surface, rendered isometrically and tinted by material.</summary>
    CollisionHeightfield,

    /// <summary>The collision lattice and material tint drawn over the authored art.</summary>
    CollisionOverArt
}

/// <summary>One rendered 2D level image: row-major RGBA, 4 bytes per pixel.</summary>
/// <remarks>
///     Wraps the producer's buffer rather than copying it — a level's art runs to
///     millions of pixels (the THPS2 GBA Hangar alone is 2064x1344).
/// </remarks>
public readonly record struct Level2dRender(int Width, int Height, byte[] Rgba);

/// <summary>
///     A level that can be shown without converting it to 3D.
/// </summary>
/// <remarks>
///     Most level formats are geometry and have no such picture; this exists for the
///     ones authored as a finished image. THPS2 GBA is the case that motivates it —
///     its levels ARE pre-rendered isometric art, and building a collision surface to
///     look at one is a detour through a lossier representation.
/// </remarks>
public interface ILevel2dSource
{
    /// <summary>The layers this level can render, in display order. Never empty.</summary>
    IReadOnlyList<Level2dLayer> Layers { get; }

    /// <summary>A short name for the level, for export stems and status lines.</summary>
    string DisplayName { get; }

    /// <summary>
    ///     Render one layer, or null when this level cannot produce it. Callers must
    ///     treat null as "nothing to show", never as an error.
    /// </summary>
    Level2dRender? Render(Level2dLayer layer);
}
