using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Cas;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>Inspection-only JSON export for PS2 and Xbox CAS polygon-removal metadata.</summary>
public static class CasCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a .cas.ps2/.cas.xbx file or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for inspection JSON files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show per-file platform, removal mask, and entry count"
        };

        var command = new Command(
            "cas",
            "Inspect PS2/Xbox CAS polygon-removal metadata as JSON (does not alter geometry)");
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
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(input) && !Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Input does not exist: {Markup.Escape(input)}[/]");
            return 1;
        }

        string[] files;
        try
        {
            files = FindFiles(input);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Failed to enumerate CAS input: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]No supported .cas.ps2 or .cas.xbx files found.[/]");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        var parsed = 0;
        var errors = 0;
        long totalEntries = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!TryGetPlatform(file, out var platform))
                    throw new InvalidDataException("Unsupported CAS platform suffix");

                var document = CasPolyRemovalFile.Parse(File.ReadAllBytes(file), platform);
                var outputPath = GetOutputPath(input, file, output);
                CasPolyRemovalJsonExporter.Write(outputPath, file, document);
                parsed++;
                totalEntries += document.Entries.Length;

                if (verbose)
                {
                    var platformName = platform == CasPolyRemovalPlatform.Ps2 ? "PS2" : "Xbox";
                    AnsiConsole.MarkupLine(
                        $"  [green]{Markup.Escape(Path.GetFileName(file))}[/]: " +
                        $"platform={platformName} removalMask=0x{document.RemovalMask:X8} " +
                        $"entries={document.Entries.Length}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                AnsiConsole.MarkupLine(
                    $"  [red]{Markup.Escape(Path.GetFileName(file))}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Parsed [green]{parsed}[/]/{files.Length} CAS files " +
            $"({totalEntries} polygon-removal records) in {stopwatch.Elapsed.TotalSeconds:F2}s" +
            (errors == 0 ? string.Empty : $" ([red]{errors} errors[/])"));
        return errors == 0 ? 0 : 1;
    }

    private static string[] FindFiles(string input)
    {
        if (File.Exists(input))
            return IsSupportedFile(input) ? [input] : [];
        if (!Directory.Exists(input))
            return [];

        return Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
            .Where(IsSupportedFile)
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsSupportedFile(string filePath)
    {
        return TryGetPlatform(filePath, out _);
    }

    internal static bool TryGetPlatform(string filePath, out CasPolyRemovalPlatform platform)
    {
        if (filePath.EndsWith(".cas.ps2", StringComparison.OrdinalIgnoreCase))
        {
            platform = CasPolyRemovalPlatform.Ps2;
            return true;
        }

        if (filePath.EndsWith(".cas.xbx", StringComparison.OrdinalIgnoreCase))
        {
            platform = CasPolyRemovalPlatform.Xbox;
            return true;
        }

        platform = default;
        return false;
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
                    $"CAS input '{filePath}' is outside input root '{inputRoot}'");
            }
        }
        else
        {
            relativePath = Path.GetFileName(filePath);
        }

        var relativeDirectory = Path.GetDirectoryName(relativePath);
        var outputName = Path.GetFileName(relativePath) + ".json";
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
                $"CAS output '{candidate}' escapes output root '{outputRoot}'");
        }

        return candidate;
    }
}
