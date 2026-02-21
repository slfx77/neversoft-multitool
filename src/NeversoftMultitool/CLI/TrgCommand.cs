using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Trg;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class TrgCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to TRG file or directory containing .trg files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for JSON files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output (show per-file node counts and types)"
        };

        var command = new Command("trg", "Parse TRG level trigger/script files to JSON");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            var files = GetTrgFiles(input);
            if (files.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No TRG files found.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            AnsiConsole.MarkupLine($"Found [green]{files.Length}[/] TRG file(s)");

            var stopwatch = Stopwatch.StartNew();
            var totalParsed = 0;
            var totalNodes = 0;
            var errors = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filename = Path.GetFileName(file);

                try
                {
                    var trg = TrgFile.Parse(file);
                    var outputPath = Path.Combine(output,
                        Path.GetFileNameWithoutExtension(file) + ".json");
                    trg.WriteJson(outputPath);

                    totalParsed++;
                    totalNodes += trg.NodeCount;

                    if (verbose)
                    {
                        var typeSummary = trg.Nodes
                            .GroupBy(n => n.Type)
                            .OrderByDescending(g => g.Count())
                            .Select(g => $"{g.Key}:{g.Count()}")
                            .Take(5);
                        AnsiConsole.MarkupLine(
                            $"  {filename}: [green]{trg.NodeCount}[/] nodes " +
                            $"(v{trg.VersionMajor}.{trg.VersionMinor}) " +
                            $"[dim]{string.Join(", ", typeSummary)}[/]");
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    if (verbose)
                        AnsiConsole.MarkupLine($"  {filename}: [red]{ex.Message}[/]");
                }
            }

            stopwatch.Stop();
            var summary = $"Parsed [green]{totalParsed}[/]/{files.Length} files " +
                          $"({totalNodes} nodes) in {stopwatch.Elapsed.TotalSeconds:F2}s";
            if (errors > 0)
                summary += $" ([red]{errors} errors[/])";
            AnsiConsole.MarkupLine(summary);

            return Task.FromResult(errors > 0 ? 1 : 0);
        });

        return command;
    }

    private static string[] GetTrgFiles(string input)
    {
        if (File.Exists(input) &&
            Path.GetExtension(input).Equals(".trg", StringComparison.OrdinalIgnoreCase))
        {
            return [input];
        }

        if (Directory.Exists(input))
        {
            return Directory.GetFiles(input)
                .Where(f => Path.GetExtension(f).Equals(".trg", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
        return [];
    }
}
