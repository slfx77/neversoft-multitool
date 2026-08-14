using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Texture.Psx;
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
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(subdirsOption),
                parseResult.GetValue(verboseOption),
                parseResult.GetValue(noDdsOption),
                cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool subdirs,
        bool verbose,
        bool noDds,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Directory not found: {Markup.Escape(input)}");
            return 1;
        }

        // Keep the established command boundary: directory-only, immediate children,
        // and the existing *.psx search pattern (no recursive or single-file expansion).
        var psxFiles = Directory.GetFiles(input, "*.psx");
        if (psxFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .psx files found in the specified directory.[/]");
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{psxFiles.Length}[/] PSX file(s)");

        var stopwatch = Stopwatch.StartNew();
        var totalTextures = 0;
        var totalWritten = 0;
        var errors = 0;

        foreach (var file in psxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = Path.GetFileName(file);
            var result = PsxLibrary.ExtractTextures(file, output, subdirs, !noDds, !noDds);

            totalTextures += result.TotalTextures;
            totalWritten += result.TexturesWritten;

            // ErrorMessage always makes the extraction unsuccessful. Invalid input can
            // still be Error+Skipped, while a non-skipped failure with no message is an
            // incomplete extraction and must also count as an error.
            var failed = result.ErrorMessage is not null
                         || (!result.Skipped && !result.Success);
            if (failed)
                errors++;

            if (verbose)
            {
                string status;
                if (result.ErrorMessage is { } errorMessage)
                    status = $"[red]error: {Markup.Escape(errorMessage)}[/]";
                else if (result.Skipped)
                    status = "[dim]skipped[/]";
                else if (result.Success)
                    status = $"[green]{result.TexturesWritten} textures[/]";
                else
                    status = $"[red]error: extracted {result.TexturesWritten}/{result.TotalTextures} textures[/]";

                AnsiConsole.MarkupLine($"  {Markup.Escape(filename)}: {status}");
            }
        }

        stopwatch.Stop();
        var summary = $"Extracted [green]{totalWritten}[/]/{totalTextures} textures " +
                      $"from {psxFiles.Length} files in {stopwatch.Elapsed.TotalSeconds:F2}s";
        if (errors > 0)
            summary += $" ([red]{errors} errors[/])";
        AnsiConsole.MarkupLine(summary);

        return errors == 0 ? 0 : 1;
    }
}
