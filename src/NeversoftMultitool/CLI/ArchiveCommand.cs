using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Archives;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class ArchiveCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to archive file (WAD, PKR, or PRE)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for extracted files",
            DefaultValueFactory = _ => "output"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("archive", "Extract files from WAD/PKR/PRE archives");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            if (!File.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
                return Task.FromResult(1);
            }

            var ext = Path.GetExtension(input).ToLowerInvariant();
            Directory.CreateDirectory(output);

            var stopwatch = Stopwatch.StartNew();
            var filesExtracted = 0;

            try
            {
                switch (ext)
                {
                    case ".wad":
                        AnsiConsole.MarkupLine("[blue]WAD[/] archive detected");
                        var wadEntries = WadArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{wadEntries.Count}[/] files");
                        WadArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"  [{current}/{total}] {wadEntries[current - 1].Name}");
                            }
                        });
                        break;

                    case ".pkr":
                        AnsiConsole.MarkupLine("[blue]PKR3[/] archive detected");
                        var pkrEntries = PkrArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{pkrEntries.Count}[/] files");
                        PkrArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"  [{current}/{total}] {pkrEntries[current - 1].FullName}");
                            }
                        });
                        break;

                    case ".pre":
                        AnsiConsole.MarkupLine("[yellow]PRE archive extraction is not yet implemented.[/]");
                        return Task.FromResult(0);

                    default:
                        AnsiConsole.MarkupLine($"[red]Unsupported archive format:[/] {ext}");
                        return Task.FromResult(1);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                return Task.FromResult(1);
            }

            stopwatch.Stop();
            AnsiConsole.MarkupLine(
                $"Extracted [green]{filesExtracted}[/] files in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }
}
