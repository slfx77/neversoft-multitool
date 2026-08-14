using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Rendering;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class GlbGifCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to an animated .glb file or directory"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for .gif files (default: next to input)"
        };
        var sizeOption = new Option<int>("-s", "--size")
        {
            Description = "Long edge of output image in pixels",
            DefaultValueFactory = _ => 512
        };
        var fpsOption = new Option<int>("--fps")
        {
            Description = "Frames per second (higher = smoother but larger file)",
            DefaultValueFactory = _ => 15
        };
        var animIndexOption = new Option<int?>("--anim-index")
        {
            Description = "Animation index inside the GLB to render (default: first animation)"
        };
        var azimuthOption = new Option<float>("--azimuth")
        {
            Description = "Camera azimuth in degrees (0=front, 90=right side)",
            DefaultValueFactory = _ => -90f
        };
        var elevationOption = new Option<float>("--elevation")
        {
            Description = "Camera elevation in degrees above horizontal",
            DefaultValueFactory = _ => 10f
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("glb-gif", "Render animated .glb files to .gif images");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(sizeOption);
        command.Options.Add(fpsOption);
        command.Options.Add(animIndexOption);
        command.Options.Add(azimuthOption);
        command.Options.Add(elevationOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var size = parseResult.GetValue(sizeOption);
            var fps = parseResult.GetValue(fpsOption);
            var animIndex = parseResult.GetValue(animIndexOption);
            var azimuth = parseResult.GetValue(azimuthOption);
            var elevation = parseResult.GetValue(elevationOption);
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(
                input, output, size, fps, animIndex, azimuth, elevation, verbose,
                cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string? output,
        int longEdge,
        int fps,
        int? animIndex,
        float azimuth,
        float elevation,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = SelectCandidatePaths(
                    Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                .ToList();
            AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] .glb files");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Input not found:[/] {Markup.Escape(input)}");
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (output == null)
        {
            var duplicateOutputs = FindDuplicateBesideSourceOutputs(files);
            if (duplicateOutputs.Length > 0)
            {
                foreach (var duplicateOutput in duplicateOutputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AnsiConsole.MarkupLine(
                        $"[red]Error:[/] Multiple GLB inputs map to beside-source output " +
                        $"[green]{Markup.Escape(duplicateOutput + ".gif")}[/]");
                }

                return 1;
            }
        }

        // With an explicit output root, only colliding stems mirror their source
        // directories. The default beside-source layout is already collision-safe
        // and retains the historical names.
        var inputRoot = Directory.Exists(input) ? input : null;
        IReadOnlyList<MeshOutputPathPlanner.PlannedOutput> outputPlan = output == null
            ? files.Select(static file => new MeshOutputPathPlanner.PlannedOutput(
                file,
                "",
                Path.GetFileNameWithoutExtension(file))).ToList()
            : MeshOutputPathPlanner.Plan(
                files,
                static file => Path.GetFileNameWithoutExtension(file),
                inputRoot);

        var sw = Stopwatch.StartNew();
        var success = 0;
        var skipped = 0;
        var fail = 0;

        foreach (var planned in outputPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = planned.File;
            var stem = planned.Stem;
            if (animIndex.HasValue)
                stem += $"_anim{animIndex.Value}";
            var plannedOutput = output == null || string.IsNullOrEmpty(planned.Subdirectory)
                ? output
                : Path.Combine(output, planned.Subdirectory);
            var gifPath = plannedOutput != null
                ? Path.Combine(plannedOutput, stem + ".gif")
                : Path.Combine(Path.GetDirectoryName(file) ?? ".", stem + ".gif");

            try
            {
                var fileSw = Stopwatch.StartNew();
                var (frameCount, duration) = GlbGifRenderer.RenderToFile(
                    file, gifPath, longEdge, fps, azimuth, elevation, animIndex);
                fileSw.Stop();

                if (frameCount == 0)
                {
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [grey]{Markup.Escape(Path.GetFileName(file))}: " +
                            "no animation, skipped[/]");
                    }
                    skipped++;
                }
                else
                {
                    success++;
                    if (verbose)
                        AnsiConsole.MarkupLine(
                            $"  [green]{Markup.Escape(Path.GetFileName(file))}[/] -> " +
                            $"[cyan]{Markup.Escape(gifPath)}[/] " +
                            $"({frameCount} frames, {duration:F2}s, {fileSw.Elapsed.TotalSeconds:F1}s render)");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine(
                    $"[red]FAIL[/] {Markup.Escape(Path.GetFileName(file))}: " +
                    Markup.Escape(ex.Message));
                fail++;
            }
        }

        sw.Stop();
        AnsiConsole.MarkupLine(
            $"Done: [green]{success}[/] rendered, [grey]{skipped}[/] skipped, " +
            $"[red]{fail}[/] failed ({sw.Elapsed.TotalSeconds:F1}s)");

        return fail > 0 ? 1 : 0;
    }

    internal static string[] SelectCandidatePaths(IEnumerable<string> paths)
    {
        return paths
            .Where(static path => Path.GetExtension(path)
                .Equals(".glb", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static string[] FindDuplicateBesideSourceOutputs(IEnumerable<string> paths)
    {
        return paths
            .Select(static path => Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(path) ?? ".",
                Path.GetFileNameWithoutExtension(path) ?? string.Empty)))
            .GroupBy(static output => output, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static output => output, StringComparer.Ordinal)
            .ToArray();
    }
}
