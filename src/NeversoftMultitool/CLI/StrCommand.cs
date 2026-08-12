using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Video;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class StrCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing STR video files (.str)"
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

        var command = new Command("str", "Convert PS1 STR (MDEC) video files to MP4");
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

        var strFiles = Directory.GetFiles(input, "*.str", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(input, "*.STR", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsCandidate)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        if (strFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .str files found in the specified directory.[/]");
            return 0;
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
        AnsiConsole.MarkupLine($"Found [green]{strFiles.Length}[/] STR file(s)");

        var stopwatch = Stopwatch.StartNew();
        var totalConverted = 0;
        var totalFailed = 0;

        foreach (var file in strFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = Markup.Escape(Path.GetFileName(file));

            if (verbose)
            {
                var probe = convertOverride == null ? StrConverter.Probe(file) : null;
                cancellationToken.ThrowIfCancellationRequested();
                var probeInfo = string.Empty;
                if (probe != null)
                {
                    var audioInfo = probe.HasAudio ? ", audio" : string.Empty;
                    probeInfo = $": {probe.ResolutionDisplay}, {probe.FrameCount} frames, " +
                                $"{probe.DurationDisplay}{audioInfo}";
                }

                AnsiConsole.MarkupLine($"  {filename}{Markup.Escape(probeInfo)}");
            }

            var result = converter(file, output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.Success)
            {
                totalConverted++;
                if (verbose)
                    AnsiConsole.MarkupLine("    → [green]OK[/]");
            }
            else
            {
                totalFailed++;
                if (verbose)
                {
                    var error = result.ErrorMessage ?? "Conversion failed";
                    AnsiConsole.MarkupLine($"    → [red]{Markup.Escape(error)}[/]");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();
        var failureInfo = totalFailed > 0 ? $", [red]{totalFailed} failed[/]" : "";
        AnsiConsole.MarkupLine(
            $"Converted [green]{totalConverted}[/]/{strFiles.Length} files{failureInfo} " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        cancellationToken.ThrowIfCancellationRequested();
        return totalFailed > 0 ? 1 : 0;

        bool IsCandidate(string file)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Quick check: skip files that aren't valid STR candidates (e.g. AFS archives).
            try
            {
                Span<byte> header = stackalloc byte[16];
                using var stream = File.OpenRead(file);
                if (stream.Read(header) < header.Length)
                    return false;

                return !(header[0] == 'A' && header[1] == 'F' &&
                         header[2] == 'S' && header[3] == 0);
            }
            catch
            {
                return false;
            }
        }
    }

    private static SfdConvertResult ConvertWithFfmpeg(
        string file,
        string destination,
        CancellationToken cancellationToken)
    {
        return StrConverter.ConvertToMp4(
            file,
            destination,
            cancellationToken: cancellationToken);
    }
}
