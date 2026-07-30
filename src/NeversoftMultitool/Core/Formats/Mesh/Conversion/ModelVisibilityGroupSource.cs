namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public enum ModelVisibilityGroupSource
{
    TriggerRange,
    AlternateGeometry,

    /// <summary>
    ///     Content gated on a game-state conditional inside a TRG entity script
    ///     (e.g. Spider-Man's C_IF_WHAT_IF easter-egg spawns).
    /// </summary>
    TriggerCondition,

    /// <summary>
    ///     A fully loader-invisible mesh rendered as the engine's forced-blend
    ///     apparition at its TRG entity placement (l1a2's Watcher head).
    /// </summary>
    HiddenApparition
}
