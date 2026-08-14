using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.QbKey;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class QbCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to QB file or directory containing QB-like files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for decompiled .q files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output (show per-file script/global counts)"
        };

        var command = new Command("qb", "Decompile QB compiled script files to source text (.q)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(verboseOption),
                cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Path not found: {Markup.Escape(input)}");
            return 1;
        }

        var files = GetQbFiles(input);
        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No QB files found.[/]");
            return 0;
        }

        var outputPaths = PlanOutputPaths(input, files, output);
        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{files.Length}[/] QB file(s)");

        var stopwatch = Stopwatch.StartNew();
        var totalParsed = 0;
        var totalScripts = 0;
        var totalGlobals = 0;
        var totalResolved = 0;
        var totalNames = 0;
        var errors = 0;

        for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[fileIndex];
            var filename = Path.GetFileName(file);

            try
            {
                var qb = QbFile.Parse(file);
                if (qb.Tokens.Count == 0)
                    throw new InvalidDataException("QB contains no recognized tokens.");

                var source = QbDecompiler.Decompile(qb);
                var outputPath = outputPaths[fileIndex];
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                    Directory.CreateDirectory(outputDir);
                File.WriteAllText(outputPath, source);

                totalParsed++;
                totalScripts += qb.ScriptCount;
                totalGlobals += qb.GlobalCount;

                if (verbose)
                {
                    // Count resolved vs unresolved names
                    var nameTokens = qb.Tokens
                        .Where(t => t.Type is QbTokenType.Name or QbTokenType.Enum)
                        .ToList();
                    var resolved = nameTokens.Count(t =>
                        qb.LocalNames.ContainsKey(t.NameChecksum) ||
                        QbKey.TryResolve(t.NameChecksum) != null);
                    totalResolved += resolved;
                    totalNames += nameTokens.Count;

                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(filename)}: [green]{qb.ScriptCount}[/] scripts, " +
                        $"[blue]{qb.GlobalCount}[/] globals, " +
                        $"[dim]{resolved}/{nameTokens.Count} names resolved[/]");
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
        var summary = $"Decompiled [green]{totalParsed}[/]/{files.Length} files " +
                      $"({totalScripts} scripts, {totalGlobals} globals) " +
                      $"in {stopwatch.Elapsed.TotalSeconds:F2}s";
        if (verbose && totalNames > 0)
        {
            var pct = (double)totalResolved / totalNames * 100;
            summary += $" — [dim]{pct:F1}% names resolved[/]";
        }

        if (errors > 0)
            summary += $" ([red]{errors} errors[/])";
        AnsiConsole.MarkupLine(summary);

        return errors > 0 ? 1 : 0;
    }

    private static string[] GetQbFiles(string input)
    {
        if (File.Exists(input) && IsQbFile(input))
        {
            return [input];
        }

        if (Directory.Exists(input))
        {
            return Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                .Where(IsQbFile)
                .ToArray();
        }

        return [];
    }

    private static bool IsQbFile(string path)
    {
        var filename = Path.GetFileName(path);
        return filename.EndsWith(".qb", StringComparison.OrdinalIgnoreCase) ||
               filename.Contains(".qb.", StringComparison.OrdinalIgnoreCase);
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
                    ScriptOutputKind.Qb))
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
