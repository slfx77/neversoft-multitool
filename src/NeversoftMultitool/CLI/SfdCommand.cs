using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Video;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class SfdCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing video files (.sfd, .pss, .bik, .bik.xen, .pmf)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted MP4 files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("sfd",
            "Convert video files to MP4 (SFD/BIK via ffmpeg, PSS with ADS audio extraction)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(
                input,
                output,
                verbose,
                cancellationToken: cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool verbose,
        Func<string, string, CancellationToken, SfdConvertResult>? convertOverride = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Directory not found: {Markup.Escape(input)}");
            return 1;
        }

        var sfdFiles = SelectCandidatePaths(
            Directory.GetFiles(input, "*", SearchOption.TopDirectoryOnly));
        cancellationToken.ThrowIfCancellationRequested();

        if (sfdFiles.Length == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No video files (.sfd, .pss, .bik, .bik.xen, .pmf) found in the specified directory.[/]");
            return 0;
        }

        var duplicateStems = FindDuplicateOutputStems(sfdFiles);
        cancellationToken.ThrowIfCancellationRequested();
        if (duplicateStems.Length > 0)
        {
            foreach (var stem in duplicateStems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Multiple video inputs map to output stem " +
                    $"[green]{Markup.Escape(stem)}[/]");
            }

            return 1;
        }

        if (convertOverride == null)
        {
            var ffmpegPath = SfdConverter.FindFfmpeg();
            cancellationToken.ThrowIfCancellationRequested();
            if (ffmpegPath == null)
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] ffmpeg not found on PATH. " +
                    "Install ffmpeg ([link]https://ffmpeg.org[/]) and ensure it's accessible.");
                return 1;
            }
        }

        var converter = convertOverride ?? ConvertWithFfmpeg;

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        cancellationToken.ThrowIfCancellationRequested();
        AnsiConsole.MarkupLine($"Found [green]{sfdFiles.Length}[/] SFD file(s)");

        var stopwatch = Stopwatch.StartNew();
        var totalConverted = 0;
        var totalFailed = 0;

        foreach (var file in sfdFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = Markup.Escape(Path.GetFileName(file));
            var result = converter(file, output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.Success)
            {
                totalConverted++;
                if (verbose)
                {
                    var probe = convertOverride == null ? SfdConverter.Probe(file) : null;
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = probe != null
                        ? $"{probe.ResolutionDisplay}, {probe.DurationDisplay}"
                        : "OK";
                    AnsiConsole.MarkupLine(
                        $"  {filename}: [green]{Markup.Escape(info)}[/]");
                }
            }
            else
            {
                totalFailed++;
                if (verbose)
                {
                    var error = result.ErrorMessage ?? "Conversion failed";
                    AnsiConsole.MarkupLine(
                        $"  {filename}: [red]{Markup.Escape(error)}[/]");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();
        var failureInfo = totalFailed > 0 ? $", [red]{totalFailed} failed[/]" : "";
        AnsiConsole.MarkupLine(
            $"Converted [green]{totalConverted}[/]/{sfdFiles.Length} files{failureInfo} " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        cancellationToken.ThrowIfCancellationRequested();
        return totalFailed > 0 ? 1 : 0;
    }

    internal static string[] SelectCandidatePaths(IEnumerable<string> paths)
    {
        return paths
            .Where(static path => FfmpegVideoFormats.IsFfmpegVideo(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static string[] FindDuplicateOutputStems(IEnumerable<string> paths)
    {
        return paths
            .GroupBy(
                // Must use the same compound-suffix rule the converter names its
                // output with, or foo.bik and foo.bik.xen produce two distinct
                // keys but one output file — a silent overwrite instead of the
                // duplicate-stem error this guard exists to raise.
                static path => FfmpegVideoFormats.GetOutputStem(path),
                StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static stem => stem, StringComparer.Ordinal)
            .ToArray();
    }

    private static SfdConvertResult ConvertWithFfmpeg(
        string file,
        string destination,
        CancellationToken cancellationToken)
    {
        return SfdConverter.ConvertToMp4(
            file,
            destination,
            cancellationToken: cancellationToken);
    }
}
