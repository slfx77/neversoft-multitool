using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Trg;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class TrgCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to TRG file or directory containing TRG-like files"
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

            return Task.FromResult(Execute(input, output, verbose, cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {Markup.Escape(input)}");
            return 1;
        }

        var files = GetTrgFiles(input);
        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No TRG files found.[/]");
            return 0;
        }

        var outputPaths = PlanOutputPaths(input, files, output);
        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{files.Length}[/] TRG file(s)");

        var stopwatch = Stopwatch.StartNew();
        var totalParsed = 0;
        var totalNodes = 0;
        var errors = 0;

        for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[fileIndex];
            var filename = Path.GetFileName(file);

            try
            {
                var trg = TrgFile.Parse(file);
                var outputPath = outputPaths[fileIndex];
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                    Directory.CreateDirectory(outputDir);
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
                        $"  {Markup.Escape(filename)}: [green]{trg.NodeCount}[/] nodes " +
                        $"(v{trg.VersionMajor}.{trg.VersionMinor}) " +
                        $"[dim]{string.Join(", ", typeSummary)}[/]");
                }
            }
            catch (Exception ex)
            {
                errors++;
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(filename)}: [red]{Markup.Escape(ex.Message)}[/]");
                }
            }
        }

        stopwatch.Stop();
        var summary = $"Parsed [green]{totalParsed}[/]/{files.Length} files " +
                      $"({totalNodes} nodes) in {stopwatch.Elapsed.TotalSeconds:F2}s";
        if (errors > 0)
            summary += $" ([red]{errors} errors[/])";
        AnsiConsole.MarkupLine(summary);

        return errors > 0 ? 1 : 0;
    }

    private static string[] GetTrgFiles(string input)
    {
        if (File.Exists(input) && IsTrgFile(input))
        {
            return [input];
        }

        if (Directory.Exists(input))
        {
            return Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                .Where(IsTrgFile)
                .ToArray();
        }

        return [];
    }

    private static bool IsTrgFile(string path)
    {
        var filename = Path.GetFileName(path);
        return filename.EndsWith(".trg", StringComparison.OrdinalIgnoreCase) ||
               filename.Contains(".trg.", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] PlanOutputPaths(
        string inputRoot,
        IReadOnlyList<string> files,
        string outputRoot)
    {
        var inputIsDirectory = Directory.Exists(inputRoot);
        var candidates = files
            .Select((file, index) =>
            {
                var relativePath = inputIsDirectory
                    ? Path.GetRelativePath(inputRoot, file)
                    : Path.GetFileName(file);
                return new OutputCandidate(
                    index,
                    file,
                    Path.GetDirectoryName(relativePath) ?? "");
            })
            .ToArray();

        var paths = new string[files.Count];
        foreach (var directoryGroup in candidates.GroupBy(
                     static candidate => candidate.RelativeDirectory,
                     StringComparer.Ordinal))
        {
            var group = directoryGroup.ToArray();
            var outputNames = ScriptOutputPathPlanner.Plan(group
                .Select(static candidate => new ScriptOutputPathInput(
                    candidate.SourcePath,
                    ScriptOutputKind.Trg))
                .ToArray());

            for (var index = 0; index < group.Length; index++)
            {
                var candidate = group[index];
                paths[candidate.OriginalIndex] = string.IsNullOrEmpty(candidate.RelativeDirectory)
                    ? Path.Combine(outputRoot, outputNames[index])
                    : Path.Combine(outputRoot, candidate.RelativeDirectory, outputNames[index]);
            }
        }

        return paths;
    }

    private readonly record struct OutputCandidate(
        int OriginalIndex,
        string SourcePath,
        string RelativeDirectory);
}
