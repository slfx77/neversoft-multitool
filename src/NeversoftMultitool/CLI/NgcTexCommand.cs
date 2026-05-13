using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class NgcTexCommand
{
    private static readonly string[] SupportedSuffixes = [".tex.ngc"];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a GameCube TEX file (.tex.ngc) or directory containing them"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for extracted PNG files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("ngctex", "Extract textures from GameCube TEX dictionaries to PNG");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, verbose));
        });

        return command;
    }

    private static int Execute(string input, string output, bool verbose)
    {
        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(IsNgcTextureFile)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No GameCube TEX files found.[/]");
            return 0;
        }

        var (supported, unsupported) = FormatProbe.PartitionFiles(files, FormatProbe.ProbeTexture);
        if (unsupported.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"Found [green]{files.Count}[/] files " +
                $"([green]{supported.Count}[/] supported, [yellow]{unsupported.Count}[/] unsupported)");
            foreach (var (fileName, reason) in unsupported)
            {
                AnsiConsole.MarkupLine($"  [yellow]\u26a0[/] {Markup.Escape(fileName)}: {Markup.Escape(reason)}");
            }
        }

        if (supported.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No supported GameCube TEX files to process.[/]");
            return 0;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Processing [green]{supported.Count}[/] GameCube TEX file(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var totalTextures = 0;

        foreach (var file in supported)
        {
            var fileName = Path.GetFileName(file);
            var stem = OrdinalFileName.StripCompoundSuffix(fileName, SupportedSuffixes);
            var result = NgcTexFile.Parse(file);

            if (!result.Success)
            {
                failed++;
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  {fileName}: [red]{Markup.Escape(result.ErrorMessage ?? "Unknown error")}[/]");
                }

                continue;
            }

            var written = NgcTexFile.SaveAllAsPng(result, output, stem);
            totalTextures += written;
            converted++;

            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  {fileName}: [green]{result.Textures.Count} textures, {written} PNGs[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{supported.Count} files " +
            $"({totalTextures:N0} textures, {failed} failed) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    private static bool IsNgcTextureFile(string path)
    {
        return OrdinalFileName.HasAnySuffix(Path.GetFileName(path), SupportedSuffixes);
    }
}
