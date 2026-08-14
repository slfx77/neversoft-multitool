using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Rle;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class RleCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing .rle/.bmr/.zlb/.bmp/.tga files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted PNG files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var widthOption = new Option<int>("-w", "--width")
        {
            Description = "Image width in pixels for RLE/BMR (0 = auto-detect; ignored for BMP/TGA)",
            DefaultValueFactory = _ => 0
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("rle", "Convert RLE/BMR/ZLB/BMP/TGA bitmap files to PNG");
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

            return Task.FromResult(Execute(input, output, width, verbose, cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        int width,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Directory not found: {Markup.Escape(input)}");
            return 1;
        }

        var rleFiles = Directory.GetFiles(input)
            .Where(BitmapFile.IsSupportedExtension)
            .ToArray();

        if (rleFiles.Length == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No .rle, .bmr, .zlb, .bmp, or .tga files found in the specified directory.[/]");
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var outputPlan = MeshOutputPathPlanner.Plan(
            rleFiles,
            static file => Path.GetFileNameWithoutExtension(file),
            input);

        Directory.CreateDirectory(output);
        var autoDetect = width == 0;
        AnsiConsole.MarkupLine(autoDetect
            ? $"Found [green]{rleFiles.Length}[/] bitmap file(s), width=auto"
            : $"Found [green]{rleFiles.Length}[/] bitmap file(s), width={width}px");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;

        foreach (var planned in outputPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = planned.File;
            var filename = Path.GetFileName(file);
            var result = BitmapFile.Convert(
                File.ReadAllBytes(file), filename, autoDetect ? null : width);

            if (result.Success)
            {
                var plannedOutput = string.IsNullOrEmpty(planned.Subdirectory)
                    ? output
                    : Path.Combine(output, planned.Subdirectory);
                var outputFile = Path.Combine(plannedOutput, planned.Stem + ".png");
                BitmapFile.SavePng(result, outputFile);
                converted++;

                if (verbose)
                {
                    var autoTag = result.WidthAutoDetected ? " (auto)" : "";
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(filename)}: " +
                        $"[green]{result.Width}x{result.Height}[/]{autoTag}");
                }
            }
            else
            {
                failed++;
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(filename)}: " +
                        $"[red]error: {Markup.Escape(result.ErrorMessage ?? string.Empty)}[/]");
                }
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{rleFiles.Length} files in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return failed > 0 ? 1 : 0;
    }
}
