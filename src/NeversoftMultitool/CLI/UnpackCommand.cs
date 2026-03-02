using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class UnpackCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Root directory to scan for archives"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("unpack",
            "Recursively extract all archives in-place (WAD/PRE/PKR/DDX/BON)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var verbose = parseResult.GetValue(verboseOption);

            if (!Directory.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {input}");
                return Task.FromResult(1);
            }

            var stopwatch = Stopwatch.StartNew();

            // Initial scan
            var initial = RecursiveUnpacker.Scan(input);
            var pending = initial.Count(a => !a.AlreadyExtracted);
            var skipped = initial.Count(a => a.AlreadyExtracted);
            AnsiConsole.MarkupLine(
                $"Found [green]{initial.Count}[/] archives " +
                $"([green]{pending}[/] to extract, [grey]{skipped}[/] already done)");

            if (pending == 0)
            {
                AnsiConsole.MarkupLine("[grey]Nothing to extract.[/]");
                return Task.FromResult(0);
            }

            try
            {
                var results = RecursiveUnpacker.ExtractAll(
                    input,
                    onArchiveStarted: archive =>
                    {
                        if (verbose)
                        {
                            var rel = Path.GetRelativePath(input, archive.FilePath);
                            AnsiConsole.MarkupLine(
                                $"  [blue]{archive.ArchiveType}[/] {Markup.Escape(rel)}");
                        }
                    },
                    onArchiveCompleted: archive =>
                    {
                        if (verbose && archive.Error != null)
                            AnsiConsole.MarkupLine($"    [red]Error:[/] {Markup.Escape(archive.Error)}");
                    },
                    onPassDiscovered: (pass, newArchives) =>
                    {
                        AnsiConsole.MarkupLine(
                            $"Pass {pass}: [green]{newArchives.Count}[/] archives to extract");
                    },
                    ct: cancellationToken);

                stopwatch.Stop();
                var extracted = results.Count(a => a.Extracted);
                var errors = results.Count(a => a.Error != null);
                AnsiConsole.MarkupLine(
                    $"Extracted [green]{extracted}[/] archives " +
                    (errors > 0 ? $"([red]{errors}[/] errors) " : "") +
                    $"in {stopwatch.Elapsed.TotalSeconds:F2}s");
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        });

        return command;
    }
}
