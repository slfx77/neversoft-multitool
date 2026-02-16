using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Rle;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class RleCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing .rle/.bmr files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted PNG files",
            DefaultValueFactory = _ => "output"
        };
        var widthOption = new Option<int>("-w", "--width")
        {
            Description = "Image width in pixels",
            DefaultValueFactory = _ => 512
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("rle", "Convert RLE/BMR bitmap files to PNG");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(widthOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var width = parseResult.GetValue(widthOption);
            var verbose = parseResult.GetValue(verboseOption);

            if (!Directory.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {input}");
                return Task.FromResult(1);
            }

            var rleFiles = Directory.GetFiles(input)
                .Where(f => f.EndsWith(".rle", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bmr", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (rleFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No .rle or .bmr files found in the specified directory.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            AnsiConsole.MarkupLine($"Found [green]{rleFiles.Length}[/] RLE/BMR file(s), width={width}px");

            var stopwatch = Stopwatch.StartNew();
            var converted = 0;

            foreach (var file in rleFiles)
            {
                var filename = Path.GetFileName(file);
                var result = RleImage.Convert(file, width);

                if (result.Success)
                {
                    var outputFile = Path.Combine(output,
                        Path.GetFileNameWithoutExtension(filename) + ".png");
                    ImageWriter.WritePngRgb(outputFile, result.Width, result.Height, result.RgbPixels);
                    converted++;

                    if (verbose)
                    {
                        AnsiConsole.MarkupLine($"  {filename}: [green]{result.Width}x{result.Height}[/]");
                    }
                }
                else if (verbose)
                {
                    AnsiConsole.MarkupLine($"  {filename}: [red]error: {result.ErrorMessage}[/]");
                }
            }

            stopwatch.Stop();
            AnsiConsole.MarkupLine(
                $"Converted [green]{converted}[/]/{rleFiles.Length} files in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }
}
