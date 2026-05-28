using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Audio;
using Spectre.Console;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.CLI;

public static class AudioCommand
{
    private static readonly string[] SupportedExtensions =
        [".adx", ".xa", ".vab", ".kat", ".sfx", ".vag", ".pss", ".vid"];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing audio files (.adx, .xa, .vab, .vag, .kat, .sfx, .pss, .vid)"
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
            Description = "Sample rate override for VAB (default: 11025) / VAG (default: 22050)",
            DefaultValueFactory = _ => 0
        };

        var command = new Command("audio", "Convert ADX/XA/VAB/VAG/KAT/SFX/PSS/VID audio files to WAV");
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

            var allFiles = Directory.GetFiles(input);
            var audioFiles = allFiles
                .Where(f => SupportedExtensions.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            // Probe extensionless files for SPU-ADPCM audio
            var extensionlessFiles = allFiles
                .Where(f => string.IsNullOrEmpty(Path.GetExtension(f)))
                .Where(f => VagDecoder.Probe(f) != null)
                .ToList();
            audioFiles.AddRange(extensionlessFiles);

            if (audioFiles.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No supported audio files found in the specified directory.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            if (extensionlessFiles.Count > 0)
                AnsiConsole.MarkupLine(
                    $"Found [green]{audioFiles.Count}[/] audio file(s) " +
                    $"({extensionlessFiles.Count} detected as headerless SPU-ADPCM)");
            else
                AnsiConsole.MarkupLine($"Found [green]{audioFiles.Count}[/] audio file(s)");

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
                    ".vab" => VabExtractor.ExtractToWav(file, output,
                        sampleRate > 0 ? sampleRate : VabExtractor.DefaultSampleRate),
                    ".vag" => VagDecoder.ConvertToWav(file, output, sampleRate),
                    ".pss" => PssAudioExtractor.ConvertToWav(file, output),
                    ".vid" => Vid1AudioExtractor.ConvertToWav(file, output),
                    ".kat" => KatExtractor.ExtractToWav(file, output),
                    ".sfx" => SfxExtractor.ExtractToWav(file, output),
                    "" => VagDecoder.ConvertToWav(file, output, sampleRate),
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
                $"Converted [green]{totalConverted}[/]/{audioFiles.Count} files " +
                $"({totalSamples} WAV files) in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }
}
