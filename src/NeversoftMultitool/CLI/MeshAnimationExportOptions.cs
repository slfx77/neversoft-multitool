namespace NeversoftMultitool.CLI;

/// <summary>
///     The animation-related arguments the <c>mesh</c> command threads down to
///     <see cref="Core.Formats.Mesh.Conversion.MeshImportRequest" />. Bundled
///     because the export entry points already carry long positional parameter
///     lists; one record keeps them named at every hop.
/// </summary>
/// <param name="IncludeAllN64Animations">
///     Embed every eligible embedded N64 0x2A/0x2C slot.
/// </param>
/// <param name="N64AnimationIndices">
///     Exact N64 slot selection. Null or empty keeps ordinary static export.
///     Kept separate from <paramref name="IncludeAllN64Animations" /> so null
///     and empty never acquire overloaded meanings.
/// </param>
/// <param name="OneShot">
///     Expand tween-compressed clips with the RunAnim one-shot clamp instead of
///     the default CycleAnim wrap. Reaches only N64 DIRECT (0x2A) clips: the
///     <c>mesh</c> command sets no
///     <see cref="Core.Formats.Animation.PsxAnimationOptions" />, so it emits no
///     PSX animation tracks at all, and compressed 0x2C slots store every frame
///     and have no end-of-clip branch to select.
/// </param>
/// <param name="IncludeAllGbaAnimations">
///     Embed every non-empty GBA skater clip (217 of THPS2's 221).
/// </param>
/// <param name="GbaAnimationIndices">
///     Exact GBA clip selection. Null or empty keeps ordinary static export;
///     out-of-range and authored-empty indices are skipped fail-closed.
/// </param>
public sealed record MeshAnimationExportOptions(
    bool IncludeAllN64Animations = false,
    IReadOnlyList<int>? N64AnimationIndices = null,
    bool OneShot = false,
    bool IncludeAllGbaAnimations = false,
    IReadOnlyList<int>? GbaAnimationIndices = null)
{
    public static MeshAnimationExportOptions None { get; } = new();
}
