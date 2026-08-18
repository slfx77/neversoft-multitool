namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Time-of-day visibility class of a worldzone leaf, derived from THAW's
///     own authored node-name tags (<c>NightOn_NN_</c> / <c>NightOff_NN_</c>,
///     toggled by the QB corpus's <c>TOD_NightOn_NN</c> /
///     <c>TOD_NightOff_NN</c> script groups). <see cref="NightOverlay" />
///     content exists only at night (lamp glows, lit windows, bulbs);
///     <see cref="DayOverlay" /> content is turned OFF at night (baked
///     daytime light shadows); <see cref="Base" /> is always visible.
/// </summary>
public enum Ps2GeomRenderLayer
{
    Base,
    NightOverlay,
    DayOverlay
}
