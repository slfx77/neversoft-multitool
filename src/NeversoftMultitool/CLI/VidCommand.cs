using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Video;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class VidCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing VID1 video files (.vid)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted MP4 files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("vid", "Convert THAW GameCube VID1 video files to MP4");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            if (SfdConverter.FindFfmpeg() == null)
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] ffmpeg not found on PATH. " +
                    "Install ffmpeg ([link]https://ffmpeg.org[/]) and ensure it's accessible.");
                return Task.FromResult(1);
            }

            if (!Directory.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {input}");
                return Task.FromResult(1);
            }

            var vidFiles = Directory.GetFiles(input, "*.vid", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(input, "*.VID", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(static file => Vid1VideoConverter.Probe(file) != null)
                .ToArray();

            if (vidFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No valid .vid files found in the specified directory.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            AnsiConsole.MarkupLine($"Found [green]{vidFiles.Length}[/] VID1 file(s)");

            var stopwatch = Stopwatch.StartNew();
            var totalConverted = 0;

            foreach (var file in vidFiles)
            {
                var fileName = Path.GetFileName(file);
                var probe = Vid1VideoConverter.Probe(file);

                if (verbose && probe != null)
                {
                    var audioInfo = probe.HasAudio
                        ? $", {probe.AudioSampleRate} Hz, {probe.AudioChannels} ch"
                        : string.Empty;
                    AnsiConsole.MarkupLine(
                        $"  {fileName}: {probe.ResolutionDisplay}, {probe.DurationDisplay}, {probe.VariantDisplay}{audioInfo}");
                }

                var result = Vid1VideoConverter.ConvertToMp4(file, output, cancellationToken: cancellationToken);
                if (result.Success)
                {
                    totalConverted++;
                    if (verbose)
                        AnsiConsole.MarkupLine("    → [green]OK[/]");
                }
                else if (verbose)
                {
                    AnsiConsole.MarkupLine($"    → [red]{result.ErrorMessage.EscapeMarkup()}[/]");
                }
            }

            stopwatch.Stop();
            AnsiConsole.MarkupLine(
                $"Converted [green]{totalConverted}[/]/{vidFiles.Length} files " +
                $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }
}
