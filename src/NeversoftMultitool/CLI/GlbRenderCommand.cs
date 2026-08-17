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
        var cameraEyeOption = new Option<string?>(ViewPose.EyeOptionName)
        {
            Description =
                "Camera position as X,Y,Z. Selects a perspective view at that exact point " +
                "instead of azimuth/elevation framing. Press P in the app's viewer to copy " +
                "a ready-made camera line. Renders geometry and depth, not PS1 appearance"
        };
        var cameraYawOption = new Option<float>(ViewPose.YawOptionName)
        {
            Description = "Camera yaw in degrees (0 looks down -Z)",
            DefaultValueFactory = _ => ViewPose.Unsupplied
        };
        var cameraPitchOption = new Option<float>(ViewPose.PitchOptionName)
        {
            Description = "Camera pitch in degrees (positive looks up)",
            DefaultValueFactory = _ => ViewPose.Unsupplied
        };
        var cameraFovOption = new Option<float>(ViewPose.FovOptionName)
        {
            Description = $"Vertical field of view in degrees (default {ViewPose.DefaultFovDegrees})",
            DefaultValueFactory = _ => ViewPose.Unsupplied
        };
        var cameraSizeOption = new Option<string?>(ViewPose.SizeOptionName)
        {
            Description = "Output size as WxH; aspect must match the view being reproduced"
        };
        var probeOption = new Option<bool>("--probe")
        {
            Description = "List every surface along the centre ray with the gap between them"
        };
        var probeAtOption = new Option<string?>("--probe-at")
        {
            Description = "Probe through pixel X,Y instead of the centre of the frame"
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
        command.Options.Add(cameraEyeOption);
        command.Options.Add(cameraYawOption);
        command.Options.Add(cameraPitchOption);
        command.Options.Add(cameraFovOption);
        command.Options.Add(cameraSizeOption);
        command.Options.Add(probeOption);
        command.Options.Add(probeAtOption);

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

            if (!ViewPose.TryCreate(
                    parseResult.GetValue(cameraEyeOption),
                    parseResult.GetValue(cameraYawOption),
                    parseResult.GetValue(cameraPitchOption),
                    parseResult.GetValue(cameraFovOption),
                    parseResult.GetValue(cameraSizeOption),
                    size,
                    out var pose,
                    out var poseError))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(poseError!)}");
                return Task.FromResult(1);
            }

            if (!TryCreateProbeRequest(
                    parseResult.GetValue(probeOption),
                    parseResult.GetValue(probeAtOption),
                    pose,
                    out var probe,
                    out var probeError))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(probeError!)}");
                return Task.FromResult(1);
            }

            return Task.FromResult(Execute(
                input, output, size, azimuth, elevation, preset, animIndex, time, verbose,
                cancellationToken, pose, probe));
        });

        return command;
    }

    /// <summary>
    ///     Resolve the probe pixel, defaulting to the centre of the frame.
    /// </summary>
    /// <remarks>
    ///     A probe needs an origin and a direction, so it is only meaningful with an
    ///     explicit camera — the azimuth/elevation path has neither.
    /// </remarks>
    internal static bool TryCreateProbeRequest(
        bool probe, string? probeAt, ViewPose? pose,
        out ProbeRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (!probe && string.IsNullOrWhiteSpace(probeAt))
            return true;

        if (pose is not { } camera)
        {
            error = $"--probe requires a camera; pass {ViewPose.EyeOptionName}=X,Y,Z.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(probeAt))
        {
            request = new ProbeRequest(camera.Width / 2, camera.Height / 2);
            return true;
        }

        var parts = probeAt.Split(',');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var x) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var y))
        {
            error = "--probe-at must be two comma-separated pixel coordinates, e.g. --probe-at=725,450";
            return false;
        }

        if (x < 0 || y < 0 || x >= camera.Width || y >= camera.Height)
        {
            error = $"--probe-at must lie inside the {camera.Width}x{camera.Height} frame.";
            return false;
        }

        request = new ProbeRequest(x, y);
        return true;
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
        CancellationToken cancellationToken = default,
        ViewPose? pose = null,
        ProbeRequest? probe = null)
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

        if (pose != null && !string.IsNullOrWhiteSpace(preset))
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] --preset renders fixed orbit views and cannot be combined " +
                $"with {ViewPose.EyeOptionName}.");
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
                    var angleLabel = pose is { } camera
                        ? $"eye={camera.Eye.X:0.##},{camera.Eye.Y:0.##},{camera.Eye.Z:0.##}"
                        : $"az={view.Azimuth:0.##}, el={view.Elevation:0.##}";
                    AnsiConsole.MarkupLine(
                        $"Rendering [cyan]{Markup.Escape(Path.GetFileName(file))}[/] " +
                        $"({Markup.Escape(view.Name)}, {angleLabel}) -> " +
                        $"[cyan]{Markup.Escape(pngPath)}[/]");
                }

                try
                {
                    GlbRenderer.RenderToFile(
                        file, pngPath, longEdge, view.Azimuth, view.Elevation, animIndex, time, pose);
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

            if (probe is { } request && pose is { } probeCamera)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var scene = GlbRenderer.LoadScene(file, animIndex, time);
                    var direction = ViewProbe.RayDirection(
                        probeCamera, request.PixelX, request.PixelY);
                    ProbeReporter.Report(
                        file, probeCamera, request,
                        ViewProbe.Cast(scene, probeCamera.Eye, direction));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Probe failed[/] {Markup.Escape(Path.GetFileName(file))}: " +
                        Markup.Escape(ex.Message));
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
