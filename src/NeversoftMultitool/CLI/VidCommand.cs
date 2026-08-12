using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Vid1;
using NeversoftMultitool.Core.Formats.Video;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class VidCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing VID1 video files (.vid)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted MP4 or PNG files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var framesOption = new Option<bool>("--frames")
        {
            Description = "Write native decoded PNG frames instead of MP4 files"
        };

        var command = new Command(
            "vid",
            "Convert THAW GameCube VID1 video files to MP4 or decoded PNG frames");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(framesOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var writeFrames = parseResult.GetValue(framesOption);

            return Task.FromResult(Execute(
                input,
                output,
                verbose,
                writeFrames,
                cancellationToken: cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool verbose,
        bool writeFrames,
        Func<string, Vid1VideoProbeResult?>? probeOverride = null,
        Func<string, string, bool, CancellationToken, SfdConvertResult>? convertOverride = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Directory not found: {Markup.Escape(input)}");
            return 1;
        }

        var candidates = Directory.GetFiles(input, "*.vid", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(input, "*.VID", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var probeFile = probeOverride ?? Vid1VideoConverter.Probe;
        var vidFiles = new List<(string File, Vid1VideoProbeResult Probe)>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = probeFile(candidate);
            cancellationToken.ThrowIfCancellationRequested();
            if (probe != null)
                vidFiles.Add((candidate, probe));
        }

        if (vidFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No valid .vid files found in the specified directory.[/]");
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
        AnsiConsole.MarkupLine($"Found [green]{vidFiles.Count}[/] VID1 file(s)");

        var stopwatch = Stopwatch.StartNew();
        var totalConverted = 0;
        var totalFailed = 0;

        foreach (var (file, probe) in vidFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Markup.Escape(Path.GetFileName(file));

            if (verbose)
            {
                var audioInfo = probe.HasAudio
                    ? $", {probe.AudioSampleRate} Hz, {probe.AudioChannels} ch"
                    : string.Empty;
                var probeInfo = $"{probe.ResolutionDisplay}, {probe.DurationDisplay}, " +
                                $"{probe.VariantDisplay}{audioInfo}";
                AnsiConsole.MarkupLine($"  {fileName}: {Markup.Escape(probeInfo)}");
            }

            var result = converter(file, output, writeFrames, cancellationToken);
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
            $"{(writeFrames ? "Exported frames from" : "Converted")} " +
            $"[green]{totalConverted}[/]/{vidFiles.Count} files{failureInfo} " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        cancellationToken.ThrowIfCancellationRequested();
        return totalFailed > 0 ? 1 : 0;

    }

    private static SfdConvertResult ConvertWithFfmpeg(
        string file,
        string destination,
        bool writeFrames,
        CancellationToken cancellationToken)
    {
        return writeFrames
            ? Vid1VideoConverter.DecodeNativeFrames(file, destination, cancellationToken)
            : Vid1VideoConverter.ConvertToMp4(
                file,
                destination,
                cancellationToken: cancellationToken);
    }
}
