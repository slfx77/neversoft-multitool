using System.CommandLine;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Produces an inspection-only JSON manifest for a paired N64 Sound Tools
///     PTR/WBK bank. Standalone files must be paired explicitly; a .z64 input
///     is paired only when its carve has exactly one of each content magic.
/// </summary>
public static class N64AudioInspectCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a Sound Tools PTR file or supported big-endian .z64 ROM"
        };
        var waveOption = new Option<string?>("--wave")
        {
            Description = "Explicit paired WBK path (required for a standalone PTR input)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output inspection JSON path",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "n64-sound-tools.json")
        };

        var command = new Command(
            "n64-audio-inspect",
            "Inspect a paired N64 Sound Tools PTR/WBK bank as JSON (no audio decode)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(waveOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(waveOption),
                parseResult.GetValue(outputOption)!));
        });
        return command;
    }

    internal static int Execute(string input, string? wavePath, string jsonPath)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        try
        {
            var sources = N64SoundToolsInputResolver.Resolve(input, wavePath);
            // Complete both-file validation before touching the destination.
            // The subsequent ordinary text-file write is not transactional.
            var bank = N64SoundToolsBank.Parse(sources.PointerData, sources.WaveData);
            N64SoundToolsBankJsonExporter.Write(
                jsonPath,
                sources.PointerSource,
                sources.WaveSource,
                bank);
            AnsiConsole.MarkupLine(
                $"Wrote [green]{bank.PointerBank.Waves.Count}[/] Sound Tools wave descriptors " +
                $"([green]{bank.PointerBank.Waves.Count(static wave => wave.Loop != null)}[/] loops) " +
                $"to [green]{Markup.Escape(jsonPath)}[/]");
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

    internal static N64SoundToolsInputSources ResolveSources(string input, string? wavePath)
        => N64SoundToolsInputResolver.Resolve(input, wavePath);

    internal static N64SoundToolsInputSources SelectCarvedPair(
        IReadOnlyList<N64AssetCarver.CarvedAsset> assets)
        => N64SoundToolsInputResolver.SelectCarvedPair(assets);
}
