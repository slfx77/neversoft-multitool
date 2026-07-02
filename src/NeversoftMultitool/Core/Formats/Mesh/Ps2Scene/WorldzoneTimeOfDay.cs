namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

/// <summary>
///     Time-of-day filter applied to THAW worldzone exports. Day excludes
///     dusk/night-overlay leaves; Night and All include them. The classification
///     comes from <see cref="Geom.Ps2GeomRenderSemantics" />.
/// </summary>
public enum WorldzoneTimeOfDay
{
    All,
    Day,
    Night
}
