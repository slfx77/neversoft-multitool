namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Minimum runtime-side character animation support implied by a PSX
///     animation bank. This is not always enough to identify the original
///     game, but it records when a file needs newer runtime field widths.
/// </summary>
public enum PsxCharacterRuntimeRevision
{
    Unknown,

    /// <summary>
    ///     THPS/THPS2-style character runtime is sufficient: animation slot
    ///     indices fit in one byte and subobject masks fit the classic path.
    /// </summary>
    ClassicSuper,

    /// <summary>
    ///     Requires Spider-Man-era widened animation slot/cache fields because
    ///     at least one animation index exceeds 255.
    /// </summary>
    ExtendedAnimSlots
}
