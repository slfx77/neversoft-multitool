namespace NeversoftMultitool.Core.Formats.Animation;

internal sealed record PsxAnimationBankDecodeResult(
    PsxAnimationBankInfo Bank,
    IReadOnlyList<(string Name, PsxAnimation Animation)> Animations,
    IReadOnlyList<PsxAnimationDecodeDiagnostic> Diagnostics);
