using System.CommandLine;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Writes an inspection-only manifest for one N64 Sound Tools BFX effects
///     bank, its conservative initial-event metadata, and its explicit or
///     uniquely carved PTR descriptor binding.
/// </summary>
public static class N64AudioFxInspectCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a Sound Tools BFX file or supported big-endian .z64 ROM"
        };
        var pointerOption = new Option<string?>("--pointer")
        {
            Description = "Explicit PTR path (required for a standalone BFX input)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output BFX inspection JSON path",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "n64-sound-tools-fx.json")
        };

        var command = new Command(
            "n64-audio-fx-inspect",
            "Inspect N64 Sound Tools BFX initial events and initial-wave-to-PTR bindings as JSON");
        command.Arguments.Add(inputArgument);
        command.Options.Add(pointerOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(pointerOption),
                parseResult.GetValue(outputOption)!));
        });
        return command;
    }

    internal static int Execute(string input, string? pointerPath, string jsonPath)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        try
        {
            // Resolve and validate the complete BFX/PTR binding before touching
            // the destination. The ordinary JSON write is not transactional.
            var sources = N64SoundToolsFxInputResolver.Resolve(input, pointerPath);
            RejectCanonicalSourcePath(input, pointerPath, jsonPath);
            N64SoundToolsFxBankJsonExporter.Write(
                jsonPath,
                sources.FxBankSource,
                sources.PointerSource,
                sources.PointerBindingBasis,
                sources.FxBank,
                sources.PointerBank);
            AnsiConsole.MarkupLine(
                $"Wrote [green]{sources.FxBank.Components.Count}[/] opaque Sound Tools BFX component slices " +
                $"and [green]{sources.FxBank.LocalWaveMap.Count}[/] local-wave-to-PTR bindings to " +
                $"[green]{Markup.Escape(jsonPath)}[/] (initial events and exact continuation suffixes " +
                "classified conservatively; all other bytecode and cue ownership remain unresolved)");
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

    private static void RejectCanonicalSourcePath(
        string input,
        string? pointerPath,
        string outputPath)
    {
        // This guards normalized path aliases. Symlink/hard-link identity is a
        // separate filesystem-level overwrite policy.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var canonicalOutput = Path.GetFullPath(outputPath);

        if (string.Equals(canonicalOutput, Path.GetFullPath(input), comparison) ||
            pointerPath != null && string.Equals(
                canonicalOutput,
                Path.GetFullPath(pointerPath),
                comparison))
        {
            throw new InvalidDataException(
                "output path resolves to the same canonical path as an input BFX/PTR/ROM source");
        }
    }

    internal static N64SoundToolsFxInputSources ResolveSources(string input, string? pointerPath) =>
        N64SoundToolsFxInputResolver.Resolve(input, pointerPath);

    internal static N64SoundToolsFxInputSources SelectCarvedSources(
        IReadOnlyList<N64AssetCarver.CarvedAsset> assets) =>
        N64SoundToolsFxInputResolver.SelectCarvedSources(assets);
}
