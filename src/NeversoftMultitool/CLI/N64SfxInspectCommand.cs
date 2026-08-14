using System.CommandLine;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Writes an inspection-only aggregate manifest for a standalone raw N64
///     SFX cue table or every structurally valid cue table carved from a
///     supported N64 ROM. This route does not join cues to BFX/PTR data.
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
            "Inspect strict raw N64 SFX cue tables as JSON (no cue mapping or playback)");
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
                sources.Banks);
            AnsiConsole.MarkupLine(
                $"Wrote [green]{sources.Banks.Count}[/] raw N64 SFX cue banks " +
                $"([green]{sources.Banks.Sum(static source => source.Bank.Records.Count)}[/] records) " +
                $"to [green]{Markup.Escape(outputPath)}[/] " +
                "(cue mapping, sample rate, pitch application, and playback remain unresolved/not applied)");
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

            return new N64SfxCueInputSources(
                Path.GetFileName(input),
                N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
                SelectCarvedBanks(assets));
        }

        if (classification is not null)
            throw new InvalidDataException(classification);

        var bank = N64SfxCueBank.Parse(File.ReadAllBytes(input));
        var source = Path.GetFileName(input);
        return new N64SfxCueInputSources(
            source,
            N64SfxCueBankJsonExporter.ExplicitFileSelection,
            [new N64SfxCueBankSource(source, bank)]);
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
    IReadOnlyList<N64SfxCueBankSource> Banks);
