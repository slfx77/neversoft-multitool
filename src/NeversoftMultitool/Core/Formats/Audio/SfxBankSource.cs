namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Where a resolved companion bank lives. <c>BankPath</c> is the real on-disk
///     path when the SFX was loaded from the filesystem; <c>BankData</c> is the
///     companion bytes when loaded from an archive. Exactly one is non-empty.
/// </summary>
internal sealed record SfxBankSource(
    string BankPath,
    string BankFormat,
    IReadOnlyList<SfxBankSample> Samples,
    byte[]? BankData = null);
