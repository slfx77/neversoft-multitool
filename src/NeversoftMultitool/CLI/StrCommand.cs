using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Video;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class StrCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing STR video files (.str)"
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

        var command = new Command("str", "Convert PS1 STR (MDEC) video files to MP4");
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

            var strFiles = Directory.GetFiles(input, "*.str", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(input, "*.STR", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f =>
                {
                    // Quick check: skip files that aren't valid STR (e.g. AFS archives)
                    try
                    {
                        var header = new byte[16];
                        using var fs = File.OpenRead(f);
                        if (fs.Read(header, 0, 16) < 16) return false;
                        // Reject AFS archives
                        return !(header[0] == 'A' && header[1] == 'F' && header[2] == 'S' && header[3] == 0);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToArray();

            if (strFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No .str files found in the specified directory.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            AnsiConsole.MarkupLine($"Found [green]{strFiles.Length}[/] STR file(s)");

            var stopwatch = Stopwatch.StartNew();
            var totalConverted = 0;

            foreach (var file in strFiles)
            {
                var filename = Path.GetFileName(file);

                if (verbose)
                {
                    var probe = StrConverter.Probe(file);
                    if (probe != null)
                    {
                        AnsiConsole.MarkupLine(
                            $"  {filename}: {probe.ResolutionDisplay}, {probe.FrameCount} frames, " +
                            $"{probe.DurationDisplay}{(probe.HasAudio ? ", audio" : "")}");
                    }
                }

                var result = StrConverter.ConvertToMp4(file, output,
                    cancellationToken: cancellationToken);

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
                $"Converted [green]{totalConverted}[/]/{strFiles.Length} files " +
                $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }
}
