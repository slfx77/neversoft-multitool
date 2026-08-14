using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Rendering;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class GlbRenderCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a .glb file or directory containing .glb files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for .png files (default: next to input)"
        };
        var sizeOption = new Option<int>("-s", "--size")
        {
            Description = "Long edge of output image in pixels (short edge from model aspect ratio)",
            DefaultValueFactory = _ => 512
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
        var presetOption = new Option<string?>("--preset")
        {
            Description = "Named camera preset. object-review renders five fixed views for placement checks"
        };
        var animIndexOption = new Option<int?>("--anim-index")
        {
            Description = "Animation index inside the GLB to render as a still frame"
        };
        var timeOption = new Option<float?>("--time")
        {
            Description = "Animation time in seconds to render with --anim-index (default: 0)"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("glb-render", "Render .glb files to .png images");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(sizeOption);
        command.Options.Add(azimuthOption);
        command.Options.Add(elevationOption);
        command.Options.Add(presetOption);
        command.Options.Add(animIndexOption);
        command.Options.Add(timeOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var size = parseResult.GetValue(sizeOption);
            var azimuth = parseResult.GetValue(azimuthOption);
            var elevation = parseResult.GetValue(elevationOption);
            var preset = parseResult.GetValue(presetOption);
            var animIndex = parseResult.GetValue(animIndexOption);
            var time = parseResult.GetValue(timeOption);
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(
                input, output, size, azimuth, elevation, preset, animIndex, time, verbose,
                cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string? output,
        int longEdge,
        float azimuth,
        float elevation,
        string? preset,
        int? animIndex,
        float? time,
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

        if (!TryGetViews(preset, azimuth, elevation, out var views))
        {
            AnsiConsole.MarkupLine(
                $"[red]Unknown preset:[/] {Markup.Escape(preset!)} ([grey]supported: object-review[/])");
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
                        $"[green]{Markup.Escape(duplicateOutput + ".png")}[/]");
                }

                return 1;
            }
        }

        // With an explicit output root, only colliding stems mirror their source
        // directories. The default beside-source layout is already collision-safe
        // and retains the filesystem enumeration order and historical names.
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
        var fail = 0;
        var useViewSuffix = views.Count > 1;

        foreach (var planned in outputPlan)
        {
            var file = planned.File;
            var plannedOutput = output == null || string.IsNullOrEmpty(planned.Subdirectory)
                ? output
                : Path.Combine(output, planned.Subdirectory);

            foreach (var view in views)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pngPath = GetOutputPath(
                    file,
                    plannedOutput,
                    planned.Stem,
                    view,
                    useViewSuffix,
                    animIndex,
                    time);
                if (verbose)
                {
                    var angleLabel = $"az={view.Azimuth:0.##}, el={view.Elevation:0.##}";
                    AnsiConsole.MarkupLine(
                        $"Rendering [cyan]{Markup.Escape(Path.GetFileName(file))}[/] " +
                        $"({Markup.Escape(view.Name)}, {angleLabel}) -> " +
                        $"[cyan]{Markup.Escape(pngPath)}[/]");
                }

                try
                {
                    GlbRenderer.RenderToFile(
                        file, pngPath, longEdge, view.Azimuth, view.Elevation, animIndex, time);
                    success++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]FAIL[/] {Markup.Escape(Path.GetFileName(file))} " +
                        $"({Markup.Escape(view.Name)}): {Markup.Escape(ex.Message)}");
                    fail++;
                }
            }
        }

        sw.Stop();
        AnsiConsole.MarkupLine(
            $"Done: [green]{success}[/] rendered, [red]{fail}[/] failed ({sw.Elapsed.TotalSeconds:F1}s)");

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

    private static bool TryGetViews(string? preset, float azimuth, float elevation,
        out IReadOnlyList<RenderView> views)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            views = [new RenderView("default", azimuth, elevation)];
            return true;
        }

        if (string.Equals(preset, "object-review", StringComparison.OrdinalIgnoreCase))
        {
            views = GlbRenderPresets.ObjectReview;
            return true;
        }

        views = [];
        return false;
    }

    private static string GetOutputPath(
        string inputFile, string? outputDir,
        string stem,
        RenderView view, bool useViewSuffix,
        int? animIndex, float? time)
    {
        var suffix = useViewSuffix ? "_" + view.Name : "";
        if (animIndex.HasValue || time.HasValue)
        {
            var index = animIndex ?? 0;
            var clampedTime = Math.Max(0f, time ?? 0f);
            var timeLabel = clampedTime.ToString("0.###", CultureInfo.InvariantCulture);
            suffix += $"_anim{index}_t{timeLabel}".Replace('.', 'p');
        }

        if (outputDir != null)
        {
            Directory.CreateDirectory(outputDir);
            return Path.Combine(outputDir, stem + suffix + ".png");
        }

        // Default: .png next to the .glb file
        var dir = Path.GetDirectoryName(inputFile) ?? ".";
        return Path.Combine(dir, stem + suffix + ".png");
    }
}
