namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     A decoded PSX animation plus optional source-bank metadata that affects
///     diagnostic export paths.
/// </summary>
public sealed record PsxAnimationClip(
    string Name,
    PsxAnimation Animation,
    IReadOnlyList<int>? TranslationParentIndices = null);
