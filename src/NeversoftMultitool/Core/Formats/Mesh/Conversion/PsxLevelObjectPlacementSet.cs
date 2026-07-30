namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Placement resolution result enriched with the trigger nodes whose
///     scripts gate their model on "What If?" mode (<c>C_IF_WHAT_IF</c>).
///     A placement whose <see cref="PsxLevelObjectPlacement.TriggerNodeIndex" />
///     is in <see cref="WhatIfNodeIndices" /> exists in-game only when the
///     easter-egg mode is active — including a coincidence-replaced bank
///     instance, which carries the replacing node's index.
/// </summary>
internal readonly record struct PsxLevelObjectPlacementSet(
    IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> Placements,
    IReadOnlySet<int> WhatIfNodeIndices);
