using System.CommandLine;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Writes an inspection-only aggregate manifest for a standalone raw N64
///     SFX cue table or every structurally valid cue table carved from a
///     supported N64 ROM. Exact build-pinned THPS2/THPS3/Spider-Man compiled
///     alias tables are joined to exactly identified BFX banks. This inspector
///     does not accept live THPS2 selector state, so runtime branches are
///     emitted as alternatives; all other cue fields and builds stay raw.
/// </summary>
public static class N64SfxInspectCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a raw N64 SFX cue table or supported big-endian .z64 ROM"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output aggregate raw-cue inspection JSON path",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "n64-sfx-cues.json")
        };

        var command = new Command(
            "n64-sfx-inspect",
            "Inspect strict raw N64 SFX cue tables as JSON (exact compiled THPS2/THPS3/Spider-Man alias targets and state choices where proven; no playback)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption)!));
        });
        return command;
    }

    internal static int Execute(string input, string outputPath)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        try
        {
            // Resolve and strictly materialize every selected bank before the
            // exporter creates a directory or opens the destination.
            var sources = Resolve(input);
            RejectCanonicalSourcePath(input, outputPath);
            N64SfxCueBankJsonExporter.Write(
                outputPath,
                sources.InputSource,
                sources.SelectionBasis,
                sources.Banks,
                sources.CompiledAliasMap,
                sources.EffectBankBinding);

            var recordCount = sources.Banks.Sum(static source => source.Bank.Records.Count);
            if (sources.CompiledAliasMap is { } aliasMap)
            {
                var mappingSummary = N64SfxCueMappingSummary.Create(sources.Banks, aliasMap);
                AnsiConsole.MarkupLine(
                    $"Wrote [green]{sources.Banks.Count}[/] raw N64 SFX cue banks " +
                    $"([green]{recordCount}[/] records) to [green]{Markup.Escape(outputPath)}[/]; " +
                    $"the exact build-pinned compiled alias table resolved " +
                    $"[green]{mappingSummary.ResolvedTargetCount}[/] BFX targets, with " +
                    $"{mappingSummary.ExplicitlyUnmappedCount} explicit no-target, " +
                    $"{mappingSummary.ExhaustiveStateDependentCount} state-dependent with exhaustive " +
                    $"outcome sets, {mappingSummary.StateDependentUnknownCount} state-dependent " +
                    $"retaining an unestablished outcome, and " +
                    $"{mappingSummary.OutsidePinnedTableCount} outside-table records " +
                    "(non-alias cue fields remain raw; sample rate, pitch, and playback were not applied)");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"Wrote [green]{sources.Banks.Count}[/] raw N64 SFX cue banks " +
                    $"([green]{recordCount}[/] records) to [green]{Markup.Escape(outputPath)}[/] " +
                    "(no independently proven compiled cue alias map for this build; " +
                    "sample rate, pitch, and playback were not applied)");
            }
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static void RejectCanonicalSourcePath(string input, string outputPath)
    {
        // This guards normalized path aliases. Symlink/hard-link identity is a
        // separate filesystem-level overwrite policy.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(
                Path.GetFullPath(outputPath),
                Path.GetFullPath(input),
                comparison))
        {
            throw new InvalidDataException(
                "output path resolves to the same canonical path as the input SFX/ROM source");
        }
    }

    internal static N64SfxCueInputSources Resolve(string input)
    {
        var classification = N64RomArchive.ClassifyRom(input);
        if (classification == "N64 ROM")
        {
            var rom = File.ReadAllBytes(input);
            if (!N64AssetCarver.TryCarve(rom, out var assets))
                throw new InvalidDataException("the ROM has no supported Edge of Reality master asset directory");

            if (!N64RomArchive.TryReadMasterDirectory(rom, out _, out _, out var bootTable))
                throw new InvalidDataException("the ROM master directory no longer resolves its boot table");
            var bootData = N64RomArchive.ExtractTable(rom, bootTable);
            var bootHash = Convert.ToHexString(SHA256.HashData(bootData));
            N64CompiledSfxAliasMap? compiledAliasMap = null;
            N64SfxCueEffectBankBindingProvenance? effectBankBinding = null;
            if (bootHash is N64SoundToolsRuntimeProfileResolver.Thps2BootSha256
                or N64SoundToolsRuntimeProfileResolver.Thps3BootSha256
                or N64SoundToolsRuntimeProfileResolver.SpiderManBootSha256)
            {
                var fxSources = N64SoundToolsFxInputResolver.SelectCarvedSources(assets);
                if (!N64CompiledSfxAliasMapResolver.TryResolve(
                        bootData, fxSources.FxBank.EffectCount, out compiledAliasMap))
                {
                    throw new InvalidDataException(
                        "known compiled SFX alias build did not resolve its pinned alias table");
                }
                effectBankBinding = N64SfxCueEffectBankBindingProvenance.Create(
                    fxSources.PointerBindingBasis,
                    fxSources.FxBankSource,
                    fxSources.FxBankData,
                    fxSources.PointerSource,
                    fxSources.PointerData);
            }

            return new N64SfxCueInputSources(
                Path.GetFileName(input),
                N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
                SelectCarvedBanks(assets),
                compiledAliasMap,
                effectBankBinding);
        }

        if (classification is not null)
            throw new InvalidDataException(classification);

        var bank = N64SfxCueBank.Parse(File.ReadAllBytes(input));
        var source = Path.GetFileName(input);
        return new N64SfxCueInputSources(
            source,
            N64SfxCueBankJsonExporter.ExplicitFileSelection,
            [new N64SfxCueBankSource(source, bank)],
            null,
            null);
    }

    internal static IReadOnlyList<N64SfxCueBankSource> SelectCarvedBanks(
        IReadOnlyList<N64AssetCarver.CarvedAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var banks = new List<N64SfxCueBankSource>();
        foreach (var asset in assets)
        {
            // Scan every carved asset. A suffix is not an oracle: this route
            // must remain compatible with legacy extractions where two real
            // THPS2 banks were conservatively named .bin.
            if (N64SfxCueBank.TryParse(asset.Data, out var bank))
                banks.Add(new N64SfxCueBankSource(asset.Path, bank!));
        }

        banks.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Source, right.Source));
        return banks;
    }
}

internal sealed record N64SfxCueInputSources(
    string InputSource,
    string SelectionBasis,
    IReadOnlyList<N64SfxCueBankSource> Banks,
    N64CompiledSfxAliasMap? CompiledAliasMap,
    N64SfxCueEffectBankBindingProvenance? EffectBankBinding);
