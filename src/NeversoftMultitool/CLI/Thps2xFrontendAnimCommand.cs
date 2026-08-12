using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Animation;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>Inspection-only JSON export for THPS2X frontend timeline files.</summary>
public static class Thps2XFrontendAnimCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a THPS2X frontend .ANIM file or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for inspection JSON files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show per-file root, node, and key counts"
        };

        var command = new Command(
            "thps2x-anim",
            "Inspect THPS2X frontend UI .ANIM timelines as JSON (not skeletal animation)");
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
                $"[red]Error:[/] Input not found: {Markup.Escape(input)}");
            return 1;
        }

        var files = FindFiles(input);
        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No THPS2X frontend .ANIM files found.[/]");
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        var parsed = 0;
        var errors = 0;
        var totalNodes = 0;
        var totalKeys = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var animation = Thps2XFrontendAnimFile.Parse(File.ReadAllBytes(file));
                var outputPath = GetOutputPath(input, file, output);
                Thps2XFrontendAnimJsonExporter.Write(outputPath, file, animation);
                parsed++;
                totalNodes += animation.NodeCount;
                totalKeys += animation.KeyCount;

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  [green]{Markup.Escape(Path.GetFileName(file))}[/]: roots={animation.Roots.Length} " +
                        $"nodes={animation.NodeCount} keys={animation.KeyCount} " +
                        $"duration={animation.Duration:F3}");
                }
            }
            catch (Exception ex)
            {
                errors++;
                AnsiConsole.MarkupLine(
                    $"  [red]{Markup.Escape(Path.GetFileName(file))}: " +
                    $"{Markup.Escape(ex.Message)}[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Parsed [green]{parsed}[/]/{files.Length} frontend timelines " +
            $"({totalNodes} nodes, {totalKeys} keys) in {stopwatch.Elapsed.TotalSeconds:F2}s" +
            (errors == 0 ? string.Empty : $" ([red]{errors} errors[/])"));
        return errors == 0 ? 0 : 1;
    }

    private static string[] FindFiles(string input)
    {
        if (File.Exists(input))
            return [input];
        if (!Directory.Exists(input))
            return [];

        return Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
            .Where(static file => Path.GetExtension(file)
                .Equals(".anim", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string GetOutputPath(string inputRoot, string filePath, string outputRoot)
    {
        string relativePath;
        if (Directory.Exists(inputRoot))
        {
            relativePath = Path.GetRelativePath(
                Path.GetFullPath(inputRoot),
                Path.GetFullPath(filePath));
            if (Path.IsPathRooted(relativePath)
                || relativePath.Equals("..", StringComparison.Ordinal)
                || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Frontend ANIM input '{filePath}' is outside input root '{inputRoot}'");
            }
        }
        else
        {
            relativePath = Path.GetFileName(filePath);
        }

        var relativeDirectory = Path.GetDirectoryName(relativePath);
        var outputName = Path.GetFileNameWithoutExtension(relativePath) + ".anim.json";
        var candidate = string.IsNullOrEmpty(relativeDirectory)
            ? Path.Combine(outputRoot, outputName)
            : Path.Combine(outputRoot, relativeDirectory, outputName);

        var outputRelative = Path.GetRelativePath(
            Path.GetFullPath(outputRoot),
            Path.GetFullPath(candidate));
        if (Path.IsPathRooted(outputRelative)
            || outputRelative.Equals("..", StringComparison.Ordinal)
            || outputRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || outputRelative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Frontend ANIM output '{candidate}' escapes output root '{outputRoot}'");
        }

        return candidate;
    }
}
