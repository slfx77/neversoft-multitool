using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class PsxCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing .psx files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for extracted textures",
            DefaultValueFactory = _ => "TestOutput"
        };
        var subdirsOption = new Option<bool>("--subdirs")
        {
            Description = "Create subdirectories for each .psx file"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var noDdsOption = new Option<bool>("--no-dds")
        {
            Description = "Skip DDS output for 16-bit textures (PNG only)"
        };

        var command = new Command("psx", "Extract textures from PSX model files");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(subdirsOption);
        command.Options.Add(verboseOption);
        command.Options.Add(noDdsOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var subdirs = parseResult.GetValue(subdirsOption);
            var verbose = parseResult.GetValue(verboseOption);
            var noDds = parseResult.GetValue(noDdsOption);

            if (!Directory.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {input}");
                return Task.FromResult(1);
            }

            var psxFiles = Directory.GetFiles(input, "*.psx");
            if (psxFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No .psx files found in the specified directory.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            AnsiConsole.MarkupLine($"Found [green]{psxFiles.Length}[/] PSX file(s)");

            var stopwatch = Stopwatch.StartNew();
            var totalTextures = 0;
            var totalWritten = 0;

            foreach (var file in psxFiles)
            {
                var filename = Path.GetFileName(file);
                var result = PsxLibrary.ExtractTextures(file, output, subdirs, writeDds: !noDds);

                totalTextures += result.TotalTextures;
                totalWritten += result.TexturesWritten;

                if (verbose)
                {
                    string status;
                    if (result.Skipped)
                        status = "[dim]skipped[/]";
                    else if (result.Success)
                        status = $"[green]{result.TexturesWritten} textures[/]";
                    else
                        status = $"[red]error: {result.ErrorMessage}[/]";
                    AnsiConsole.MarkupLine($"  {filename}: {status}");
                }
            }

            stopwatch.Stop();
            AnsiConsole.MarkupLine(
                $"Extracted [green]{totalWritten}[/]/{totalTextures} textures " +
                $"from {psxFiles.Length} files in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }
}
