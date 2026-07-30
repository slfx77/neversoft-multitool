namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Opts a placed-object geometry pass into ghost emission: an object whose
///     mesh has ONLY loader-invisible faces but at least one TRG entity
///     placement renders as the engine's forced-blend apparition (see
///     <see cref="PsxGhostFaces" />), behind a per-object visibility group.
///     Level primary geometry and the items pickup pass never receive these
///     options, so their invisible meshes (collision blockers, trigger
///     volumes) stay undrawn.
/// </summary>
internal sealed class PsxGhostEmissionOptions
{
    /// <summary>
    ///     Level asset hash (QbKey of the uppercased level stem) keeping ghost
    ///     visibility-group ids stable per level, mirroring the trigger-range
    ///     group id convention.
    /// </summary>
    internal required uint AssetHash { get; init; }

    /// <summary>
    ///     Caller-selected visibility states keyed by group id; a missing entry
    ///     keeps the ghost's default-enabled state.
    /// </summary>
    internal IReadOnlyDictionary<string, bool>? VisibilityOverrides { get; init; }
}
