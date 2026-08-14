using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Texture.Pvr;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class PvrCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a .pvr file or directory containing .pvr files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted PNG files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("pvr", "Convert Dreamcast PVR texture files to PNG");
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] pvrFiles;
        if (File.Exists(input))
            pvrFiles = [input];
        else if (Directory.Exists(input))
            pvrFiles = SelectCandidatePaths(
                Directory.GetFiles(input, "*", SearchOption.TopDirectoryOnly));
        else
            pvrFiles = [];

        if (pvrFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No .pvr files found");
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var duplicateStems = FindDuplicateOutputStems(pvrFiles);
        cancellationToken.ThrowIfCancellationRequested();
        if (duplicateStems.Length > 0)
        {
            foreach (var stem in duplicateStems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Multiple PVR inputs map to output stem " +
                    $"[green]{Markup.Escape(stem)}[/]");
            }

            return 1;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{pvrFiles.Length}[/] PVR file(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;

        foreach (var file in pvrFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = Path.GetFileName(file);
            var pngName = Path.ChangeExtension(filename, ".png");
            var pngPath = Path.Combine(output, pngName);

            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);

            var success = PvrFileDecoder.DecodeToPng(reader, 0, pngPath);

            if (success)
            {
                converted++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  [green]OK[/] {Markup.Escape(filename)}");
            }
            else
            {
                failed++;
                if (verbose)
                    AnsiConsole.MarkupLine(
                        $"  [red]FAIL[/] {Markup.Escape(filename)}: unsupported format");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{pvrFiles.Length} files in {stopwatch.Elapsed.TotalSeconds:F2}s");

        if (failed > 0)
            AnsiConsole.MarkupLine($"[yellow]{failed} file(s) had unsupported formats[/]");

        return failed > 0 ? 1 : 0;
    }

    internal static string[] SelectCandidatePaths(IEnumerable<string> paths)
    {
        return paths
            .Where(static path => Path.GetExtension(path)
                .Equals(".pvr", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static string[] FindDuplicateOutputStems(IEnumerable<string> paths)
    {
        return paths
            .GroupBy(
                static path => Path.GetFileNameWithoutExtension(path) ?? string.Empty,
                StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static stem => stem, StringComparer.Ordinal)
            .ToArray();
    }
}
