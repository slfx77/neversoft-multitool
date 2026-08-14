using System.CommandLine;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Reports the executable-derived, ROM-global Sound Tools mixer output
///     profile for an evidence-matched audited final ROM. This route has
///     no standalone PTR/WBK mode and performs no wave playback.
/// </summary>
public static class N64AudioRuntimeInspectCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to an evidence-matched audited big-endian Edge of Reality .z64 ROM"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output ROM-global Sound Tools runtime profile JSON path",
            DefaultValueFactory = _ => Path.Combine(
                "TestOutput", "n64-sound-tools-runtime-profile.json")
        };

        var command = new Command(
            "n64-audio-runtime-inspect",
            "Inspect the ROM-global Sound Tools mixer/output profile as JSON " +
            "(not a per-wave or cue rate)");
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

    internal static int Execute(string input, string outputPath) =>
        Execute(input, outputPath, Resolve);

    internal static int Execute(
        string input,
        string outputPath,
        Func<string, N64SoundToolsRuntimeProfile> resolve)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        try
        {
            var profile = resolve(input);
            RejectCanonicalSourcePath(input, outputPath);
            N64SoundToolsRuntimeProfileJsonExporter.Write(
                outputPath,
                Path.GetFileName(input),
                profile);
            AnsiConsole.MarkupLine(
                $"Wrote ROM-global Sound Tools mixer profile " +
                $"([green]{profile.MixerProfile.AiFrequencyReturnHz} Hz[/] hardware return) " +
                $"to [green]{Markup.Escape(outputPath)}[/] " +
                "(per-wave/cue rate remains unresolved; pitch, loops, and playback were not applied)");
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
                "output path resolves to the same canonical path as the input ROM source");
        }
    }

    internal static N64SoundToolsRuntimeProfile Resolve(string input)
    {
        var classification = N64RomArchive.ClassifyRom(input);
        if (classification != "N64 ROM")
        {
            throw new InvalidDataException(classification is null
                ? "runtime profile input must be a big-endian .z64 ROM"
                : classification);
        }

        var rom = File.ReadAllBytes(input);
        if (!N64RomArchive.TryReadMasterDirectory(rom, out _, out _, out var bootTable))
        {
            throw new InvalidDataException(
                "the ROM has no supported Edge of Reality master asset directory");
        }

        var boot = N64RomArchive.ExtractTable(rom, bootTable);
        return N64SoundToolsRuntimeProfileResolver.Resolve(rom, boot);
    }
}
