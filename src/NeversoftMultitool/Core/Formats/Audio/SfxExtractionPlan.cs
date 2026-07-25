namespace NeversoftMultitool.Core.Formats.Audio;

internal sealed record SfxExtractionPlan(SfxBankSource BankSource, IReadOnlyList<SfxCueMapping> Mappings);
