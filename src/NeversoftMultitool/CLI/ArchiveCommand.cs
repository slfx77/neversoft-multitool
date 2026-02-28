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
            Description = "Path to archive file (WAD, PKR, PRE, PRX, DDX, or BON)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for extracted files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("archive", "Extract files from WAD/PKR/PRE/PRX/DDX/BON archives");
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
                                AnsiConsole.MarkupLine($"  [[{current}/{total}]] {wadEntries[current - 1].Name}");
                            }
                        }, cancellationToken);
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
                                AnsiConsole.MarkupLine($"  [[{current}/{total}]] {pkrEntries[current - 1].FullName}");
                            }
                        }, cancellationToken);
                        break;

                    case ".ddx":
                        AnsiConsole.MarkupLine("[blue]DDX[/] texture archive detected");
                        var ddxEntries = DdxArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{ddxEntries.Count}[/] textures");
                        DdxArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"  [[{current}/{total}]] {ddxEntries[current - 1].Name}");
                            }
                        }, cancellationToken);
                        break;

                    case ".bon":
                        AnsiConsole.MarkupLine("[blue]BON[/] model archive detected");
                        var bonEntries = BonArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{bonEntries.Count}[/] textures");
                        BonArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"  [[{current}/{total}]] {bonEntries[current - 1].Name}");
                            }
                        }, cancellationToken);
                        break;

                    case ".pre" when CompressedPreArchive.IsCompressedPre(input):
                    case ".prx":
                        AnsiConsole.MarkupLine("[blue]PRE v3[/] archive detected (LZSS compressed)");
                        var compressedPreEntries = CompressedPreArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{compressedPreEntries.Count}[/] files");
                        CompressedPreArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] {compressedPreEntries[current - 1].FullName}");
                            }
                        }, cancellationToken);
                        break;

                    case ".pre":
                        AnsiConsole.MarkupLine("[blue]PRE[/] archive detected");
                        var preEntries = PreArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{preEntries.Count}[/] files");
                        PreArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"  [[{current}/{total}]] {preEntries[current - 1].Name}");
                            }
                        }, cancellationToken);
                        break;

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
