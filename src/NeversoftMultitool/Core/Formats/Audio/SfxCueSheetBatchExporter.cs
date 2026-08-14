using System.Diagnostics.CodeAnalysis;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>One owned SFX sheet presented to the bank batch exporter.</summary>
internal readonly record struct SfxCueSheetBatchInput(
    string FileName,
    string RelativePath,
    byte[] Data);

/// <summary>
///     Exports the true cue mappings from every usable sheet owned by one bank.
///     Returning false is an explicit request for the caller to extract the raw
///     bank once; this happens only when no sheet resolves to authored cues.
/// </summary>
internal static class SfxCueSheetBatchExporter
{
    internal static bool TryExtractToWav(
        IReadOnlyList<SfxCueSheetBatchInput> sheets,
        string bankStem,
        SfxExtractor.SfxBankBytes bank,
        string outputDir,
        [NotNullWhen(true)] out AudioConvertResult? result)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(bankStem);
        ArgumentNullException.ThrowIfNull(outputDir);

        var resolved = sheets
            .Where(sheet => IsTrueCueSheet(sheet, bank))
            .ToArray();
        if (resolved.Length == 0)
        {
            result = null;
            return false;
        }

        if (resolved.Length == 1)
        {
            result = SfxExtractor.ExtractToWav(
                resolved[0].Data,
                bankStem,
                bank,
                outputDir);
            return true;
        }

        var sheetStems = AudioOutputStemPlanner.Plan(resolved
            .Select(static sheet => new AudioOutputStemInput(sheet.FileName, sheet.RelativePath))
            .ToArray());
        var bankOutputDir = Path.Combine(outputDir, bankStem);
        var samplesWritten = 0;
        string? firstError = null;

        for (var index = 0; index < resolved.Length; index++)
        {
            var sheetResult = SfxExtractor.ExtractToWav(
                resolved[index].Data,
                sheetStems[index],
                bank,
                bankOutputDir);
            samplesWritten = checked(samplesWritten + sheetResult.SamplesWritten);
            firstError ??= sheetResult.ErrorMessage;
        }

        result = new AudioConvertResult
        {
            Success = samplesWritten > 0,
            SamplesWritten = samplesWritten,
            ErrorMessage = samplesWritten > 0
                ? null
                : firstError ?? "No WAV files could be extracted from the resolved SFX cue sheets"
        };
        return true;
    }

    private static bool IsTrueCueSheet(
        SfxCueSheetBatchInput sheet,
        SfxExtractor.SfxBankBytes bank)
    {
        return sheet.Data != null &&
               SfxExtractor.TryResolveSamples(sheet.Data, bank, out var resolution, out _) &&
               resolution.Kind == SfxResolutionKind.ResolvedCues;
    }
}
