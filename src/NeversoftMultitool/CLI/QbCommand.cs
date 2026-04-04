using System.CommandLine;
using System.Diagnostics;
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
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            var files = GetQbFiles(input);
            if (files.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No QB files found.[/]");
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(output);
            AnsiConsole.MarkupLine($"Found [green]{files.Length}[/] QB file(s)");

            var stopwatch = Stopwatch.StartNew();
            var totalParsed = 0;
            var totalScripts = 0;
            var totalGlobals = 0;
            var totalResolved = 0;
            var totalNames = 0;
            var errors = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filename = Path.GetFileName(file);

                try
                {
                    var qb = QbFile.Parse(file);
                    var source = QbDecompiler.Decompile(qb);
                    var outputPath = GetOutputPath(input, file, output, ".q");
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
                            $"  {filename}: [green]{qb.ScriptCount}[/] scripts, " +
                            $"[blue]{qb.GlobalCount}[/] globals, " +
                            $"[dim]{resolved}/{nameTokens.Count} names resolved[/]");
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

            return Task.FromResult(errors > 0 ? 1 : 0);
        });

        return command;
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

        AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
        return [];
    }

    private static bool IsQbFile(string path)
    {
        var filename = Path.GetFileName(path);
        return filename.EndsWith(".qb", StringComparison.OrdinalIgnoreCase) ||
               filename.Contains(".qb.", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOutputPath(string inputRoot, string filePath, string outputRoot, string outputExtension)
    {
        var relativePath = Directory.Exists(inputRoot)
            ? Path.GetRelativePath(inputRoot, filePath)
            : Path.GetFileName(filePath);
        var relativeDir = Path.GetDirectoryName(relativePath);
        var outputName = Path.GetFileNameWithoutExtension(relativePath) + outputExtension;

        return string.IsNullOrEmpty(relativeDir)
            ? Path.Combine(outputRoot, outputName)
            : Path.Combine(outputRoot, relativeDir, outputName);
    }
}
