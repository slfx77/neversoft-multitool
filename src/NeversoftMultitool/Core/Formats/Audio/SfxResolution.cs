namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Describes whether an SFX table resolved to authored cue mappings or
///     whether the resolver conservatively exposed the complete companion bank.
/// </summary>
public enum SfxResolutionKind
{
    ResolvedCues,
    FullBankFallback
}

/// <summary>
///     The classified result of resolving an SFX cue table against one explicit
///     KAT or VAB companion bank.
/// </summary>
public sealed record SfxResolution(
    SfxResolutionKind Kind,
    IReadOnlyList<SfxExtractor.SfxSampleInfo> Samples);
