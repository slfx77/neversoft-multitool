using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Collision;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Inspection-only JSON export for GameCube collision files (.col.ngc).
///     The format stores topology, flags, terrain, and BSP trees, but no
///     vertex positions — the engine binds those to the render scene's
///     vertex pool at load — so there is deliberately no GLB output here.
/// </summary>
public static class NgcColCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a GameCube .col.ngc collision file or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for inspection JSON files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show per-file object, face, and BSP counts"
        };

        var command = new Command(
            "ngccol",
            "Inspect GameCube collision (.col.ngc) structure as JSON (no vertex positions exist to convert)");
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

    internal static int Execute(string input, string output, bool verbose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(input) && !Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Input does not exist: {Markup.Escape(input)}[/]");
            return 1;
        }

        var files = FindFiles(input);
        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No GameCube .col.ngc files found.[/]");
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        var parsed = 0;
        var errors = 0;
        var totalObjects = 0;
        var totalFaces = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var scene = NgcColFile.Parse(File.ReadAllBytes(file));
                var outputPath = GetOutputPath(input, file, output);
                NgcColJsonExporter.Write(outputPath, file, scene);
                parsed++;
                totalObjects += scene.Objects.Length;
                totalFaces += scene.TotalFaces;

                if (verbose)
                {
                    var leaves = scene.Objects.Sum(static o => o.BspRoot.CountLeaves());
                    AnsiConsole.MarkupLine(
                        $"  [green]{Markup.Escape(Path.GetFileName(file))}[/]: objects={scene.Objects.Length} " +
                        $"verts={scene.TotalVerts} faces={scene.TotalFaces} bspLeaves={leaves}");
                }
            }
            catch (Exception ex)
            {
                errors++;
                AnsiConsole.MarkupLine(
                    $"  [red]{Markup.Escape(Path.GetFileName(file))}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Parsed [green]{parsed}[/]/{files.Length} GameCube collision files " +
            $"({totalObjects} objects, {totalFaces} faces) in {stopwatch.Elapsed.TotalSeconds:F2}s" +
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
            .Where(static file => file.EndsWith(".col.ngc", StringComparison.OrdinalIgnoreCase))
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
                    $"NGC COL input '{filePath}' is outside input root '{inputRoot}'");
            }
        }
        else
        {
            relativePath = Path.GetFileName(filePath);
        }

        var relativeDirectory = Path.GetDirectoryName(relativePath);
        var fileName = Path.GetFileName(relativePath);
        var outputName = fileName[..^".col.ngc".Length] + ".col.json";
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
                $"NGC COL output '{candidate}' escapes output root '{outputRoot}'");
        }

        return candidate;
    }
}
