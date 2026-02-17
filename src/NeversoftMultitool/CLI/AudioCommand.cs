using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Audio;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class AudioCommand
{
    private static readonly string[] SupportedExtensions = [".adx", ".xa", ".vab", ".kat"];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing audio files (.adx, .xa, .vab, .kat)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted WAV files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var sampleRateOption = new Option<int>("-r", "--sample-rate")
        {
            Description = "Sample rate for VAB output (default: 11025)",
            DefaultValueFactory = _ => 11025
        };

        var command = new Command("audio", "Convert ADX/XA/VAB/KAT audio files to WAV");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(sampleRateOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var sampleRate = parseResult.GetValue(sampleRateOption);

            if (!Directory.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {input}");
                return Task.FromResult(1);
            }

            var audioFiles = Directory.GetFiles(input)
                .Where(f => SupportedExtensions.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))
                .ToArray();

            if (audioFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No supported audio files found in the specified directory.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            AnsiConsole.MarkupLine($"Found [green]{audioFiles.Length}[/] audio file(s)");

            var stopwatch = Stopwatch.StartNew();
            var totalConverted = 0;
            var totalSamples = 0;

            foreach (var file in audioFiles)
            {
                var filename = Path.GetFileName(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();

                var result = ext switch
                {
                    ".adx" => AdxDecoder.ConvertToWav(file, output),
                    ".xa" => XaDecoder.ConvertToWav(file, output),
                    ".vab" => VabExtractor.ExtractToWav(file, output, sampleRate),
                    ".kat" => KatExtractor.ExtractToWav(file, output),
                    _ => new AudioConvertResult { ErrorMessage = "Unsupported format" }
                };

                if (result.Success)
                {
                    totalConverted++;
                    totalSamples += result.SamplesWritten;

                    if (verbose)
                    {
                        var detail = result.SamplesWritten > 1
                            ? $"[green]{result.SamplesWritten} samples[/]"
                            : "[green]OK[/]";
                        AnsiConsole.MarkupLine($"  {filename}: {detail}");
                    }
                }
                else if (verbose)
                {
                    AnsiConsole.MarkupLine($"  {filename}: [red]{result.ErrorMessage}[/]");
                }
            }

            stopwatch.Stop();
            AnsiConsole.MarkupLine(
                $"Converted [green]{totalConverted}[/]/{audioFiles.Length} files " +
                $"({totalSamples} WAV files) in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }
}
