using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Video;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class SfdCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing video files (.sfd, .pss, .bik)"
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

        var command = new Command("sfd",
            "Convert video files to MP4 (SFD/BIK via ffmpeg, PSS with ADS audio extraction)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            var ffmpegPath = SfdConverter.FindFfmpeg();
            if (ffmpegPath == null)
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

            string[] videoExtensions = ["*.sfd", "*.SFD", "*.pss", "*.PSS", "*.bik", "*.BIK"];
            var sfdFiles = videoExtensions
                .SelectMany(ext => Directory.GetFiles(input, ext, SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (sfdFiles.Length == 0)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]No video files (.sfd, .pss, .bik) found in the specified directory.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            AnsiConsole.MarkupLine($"Found [green]{sfdFiles.Length}[/] SFD file(s)");

            var stopwatch = Stopwatch.StartNew();
            var totalConverted = 0;

            foreach (var file in sfdFiles)
            {
                var filename = Path.GetFileName(file);
                var result = SfdConverter.ConvertToMp4(file, output,
                    cancellationToken: cancellationToken);

                if (result.Success)
                {
                    totalConverted++;
                    if (verbose)
                    {
                        var probe = SfdConverter.Probe(file);
                        var info = probe != null
                            ? $"{probe.ResolutionDisplay}, {probe.DurationDisplay}"
                            : "OK";
                        AnsiConsole.MarkupLine($"  {filename}: [green]{info}[/]");
                    }
                }
                else if (verbose)
                {
                    AnsiConsole.MarkupLine($"  {filename}: [red]{result.ErrorMessage}[/]");
                }
            }

            stopwatch.Stop();
            AnsiConsole.MarkupLine(
                $"Converted [green]{totalConverted}[/]/{sfdFiles.Length} files " +
                $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }
}
