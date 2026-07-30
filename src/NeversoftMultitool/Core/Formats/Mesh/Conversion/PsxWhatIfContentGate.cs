namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Gates Spider-Man's "What If?" easter-egg spawns out of the assembled
///     level by default. PLATFORM nodes whose scripts run <c>C_IF_WHAT_IF</c>
///     before their <c>V_MODEL_CHECKSUM</c> only create their prop when the
///     mode is active, so their placements (including a coincidence-replaced
///     bank instance — the game never shows it outside the mode) are removed
///     unless the user enables the exposed visibility group. An object whose
///     only placements were What If nodes disappears entirely.
/// </summary>
internal static class PsxWhatIfContentGate
{
    /// <summary>
    ///     Applies the gate: appends the "What If?" visibility group to
    ///     <paramref name="document" /> when any resolved placement is gated,
    ///     and returns the placements with gated entries removed while the
    ///     group is disabled (its authored default).
    /// </summary>
    internal static IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> Apply(
        ModelDocument document,
        IReadOnlyDictionary<string, bool>? visibilityOverrides,
        uint assetHash,
        PsxLevelObjectPlacementSet resolved)
    {
        var placements = resolved.Placements;
        var gatedNodes = resolved.WhatIfNodeIndices;
        if (gatedNodes.Count == 0)
            return placements;

        var gatedNodeIndices = placements.Values
            .SelectMany(static list => list)
            .Select(static placement => placement.TriggerNodeIndex)
            .Where(gatedNodes.Contains)
            .Distinct()
            .Order()
            .ToArray();
        if (gatedNodeIndices.Length == 0)
            return placements;

        var id = $"psx.whatif.{assetHash:X8}";
        var enabled = visibilityOverrides?.TryGetValue(id, out var selected) == true && selected;
        document.VisibilityGroups.Add(new ModelVisibilityGroup
        {
            Id = id,
            Label = "\"What If?\" content",
            DefaultEnabled = false,
            IsEnabled = enabled,
            Source = ModelVisibilityGroupSource.TriggerCondition,
            SourceReference = "C_IF_WHAT_IF PLATFORM nodes " + string.Join(", ", gatedNodeIndices)
        });
        if (enabled)
            return placements;

        var filtered =
            new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>(placements.Count);
        foreach (var (objectIndex, objectPlacements) in placements)
        {
            var kept = objectPlacements
                .Where(placement => !gatedNodes.Contains(placement.TriggerNodeIndex))
                .ToArray();
            if (kept.Length > 0)
                filtered[objectIndex] = kept;
        }

        return filtered;
    }
}
