using System.CommandLine;
using NeversoftMultitool.Core.Formats.GsDump;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class GsDumpCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a raw PCSX2 .gs dump or directory of .gs dumps"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for GS dump audit artifacts",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "gsdump_audit")
        };
        var pngOption = new Option<string?>("--png")
        {
            Description = "Optional screenshot PNG to compare against. Defaults to a sibling .png."
        };
        var texOption = new Option<string?>("--tex")
        {
            Description = "Optional THAW PS2 worldzone texture source for TEX0 correlation and fallback sampling"
        };
        var maxDumpsOption = new Option<int?>("--max-dumps")
        {
            Description = "Maximum number of dumps to audit from a directory"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var jsonOnlyOption = new Option<bool>("--json-only")
        {
            Description = "Only write JSON reports; skip render and diff PNG output"
        };
        var probeXOption = new Option<int?>("--probe-x")
        {
            Description = "X coordinate of the pixel to trace; pairs with --probe-y"
        };
        var probeYOption = new Option<int?>("--probe-y")
        {
            Description = "Y coordinate of the pixel to trace; pairs with --probe-x"
        };
        var probeOutOption = new Option<string?>("--probe-out")
        {
            Description = "Optional path for the probe CSV (defaults to {output}/{stem}.probe-X-Y.csv)"
        };
        var probeFbpOption = new Option<uint?>("--probe-fbp")
        {
            Description = "Filter probe to writes targeting this FBP (page address); pairs with --probe-x/y"
        };
        var maxVsyncOption = new Option<int?>("--max-vsync")
        {
            Description = "Stop replay after N VSync events (1 = first frame only)"
        };
        var saveRtDirOption = new Option<string?>("--save-rt-dir")
        {
            Description =
                "Directory to write per-draw render-target snapshots (matches PCSX2 SaveRT). Files: NNNNN_rt_BBBBB_C_NN.png"
        };
        var saveRtStartOption = new Option<int?>("--save-rt-start")
        {
            Description = "Start draw index for per-draw RT capture (default 0)"
        };
        var saveRtCountOption = new Option<int?>("--save-rt-count")
        {
            Description = "Max number of draws to snapshot (default unlimited)"
        };
        var saveRtFbpOption = new Option<uint?>("--save-rt-fbp")
        {
            Description = "Restrict per-draw RT capture to draws targeting this FBP (block address)"
        };
        var saveRtOnStateTransitionOption = new Option<bool>("--save-rt-on-state-transition")
        {
            Description =
                "Capture per-draw RT snapshots ONLY at framebuffer-state (Fbp, Fbw, Psm) transitions. Use to align with PCSX2's per-primitive-batch RT dumps."
        };

        var command = new Command("gsdump", "Audit raw PCSX2 GS dumps with a pure C# GIF/GS parser and renderer");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(pngOption);
        command.Options.Add(texOption);
        command.Options.Add(maxDumpsOption);
        command.Options.Add(verboseOption);
        command.Options.Add(jsonOnlyOption);
        command.Options.Add(probeXOption);
        command.Options.Add(probeYOption);
        command.Options.Add(probeOutOption);
        command.Options.Add(probeFbpOption);
        command.Options.Add(maxVsyncOption);
        command.Options.Add(saveRtDirOption);
        command.Options.Add(saveRtStartOption);
        command.Options.Add(saveRtCountOption);
        command.Options.Add(saveRtFbpOption);
        command.Options.Add(saveRtOnStateTransitionOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(pngOption),
                parseResult.GetValue(texOption),
                parseResult.GetValue(maxDumpsOption),
                parseResult.GetValue(verboseOption),
                parseResult.GetValue(jsonOnlyOption),
                parseResult.GetValue(probeXOption),
                parseResult.GetValue(probeYOption),
                parseResult.GetValue(probeOutOption),
                parseResult.GetValue(probeFbpOption),
                parseResult.GetValue(maxVsyncOption),
                parseResult.GetValue(saveRtDirOption),
                parseResult.GetValue(saveRtStartOption),
                parseResult.GetValue(saveRtCountOption),
                parseResult.GetValue(saveRtFbpOption),
                parseResult.GetValue(saveRtOnStateTransitionOption)));
        });

        return command;
    }

    private static int Execute(
        string input,
        string output,
        string? pngPath,
        string? texPath,
        int? maxDumps,
        bool verbose,
        bool jsonOnly,
        int? probeX,
        int? probeY,
        string? probeOut,
        uint? probeFbp,
        int? maxVsync,
        string? saveRtDir,
        int? saveRtStart,
        int? saveRtCount,
        uint? saveRtFbp,
        bool saveRtOnStateTransition)
    {
        var files = CollectFiles(input);
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No raw .gs dump files found.[/]");
            return 0;
        }

        if (pngPath != null && files.Count > 1)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --png can only be used when auditing a single dump.");
            return 1;
        }

        if (maxDumps is <= 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --max-dumps must be positive when provided.");
            return 1;
        }

        if (!probeFbp.HasValue && probeX.HasValue != probeY.HasValue)
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] --probe-x and --probe-y must both be provided unless --probe-fbp is set.");
            return 1;
        }

        if ((probeX.HasValue || probeY.HasValue || probeFbp.HasValue || probeOut != null) && files.Count > 1)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] probe options require auditing a single dump.");
            return 1;
        }

        if (maxDumps.HasValue)
            files = files.Take(maxDumps.Value).ToList();

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] raw GS dump(s)");

        var failed = 0;
        foreach (var file in files)
        {
            try
            {
                var report = GsDumpAuditRunner.Run(
                    file,
                    output,
                    new GsDumpAuditOptions
                    {
                        PngPath = pngPath,
                        TexturePath = texPath,
                        JsonOnly = jsonOnly,
                        Verbose = verbose,
                        ProbeX = probeX,
                        ProbeY = probeY,
                        ProbeFbp = probeFbp,
                        ProbeOutputPath = probeOut,
                        MaxVsync = maxVsync,
                        SaveRtDir = saveRtDir,
                        SaveRtStart = saveRtStart ?? 0,
                        SaveRtCount = saveRtCount,
                        SaveRtFbp = saveRtFbp,
                        SaveRtOnStateTransition = saveRtOnStateTransition
                    });

                PrintSummary(file, report, verbose);
            }
            catch (Exception ex)
            {
                failed++;
                AnsiConsole.MarkupLine(
                    $"  [red]fail[/] {Markup.Escape(Path.GetFileName(file))}: {Markup.Escape(ex.Message)}");
            }
        }

        if (failed == 0)
        {
            AnsiConsole.MarkupLine(
                $"GS dump audit complete: [green]{files.Count}[/] succeeded -> {Markup.Escape(output)}");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"GS dump audit complete: [green]{files.Count - failed}[/] succeeded, [red]{failed}[/] failed -> {Markup.Escape(output)}");
        return 1;
    }

    private static List<string> CollectFiles(string input)
    {
        if (File.Exists(input))
            return IsRawGs(input) ? [input] : [];

        if (!Directory.Exists(input))
            return [];

        return Directory.EnumerateFiles(input, "*.gs", SearchOption.TopDirectoryOnly)
            .Where(IsRawGs)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsRawGs(string path)
    {
        return path.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith(".gs.xz", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith(".gs.zst", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintSummary(string file, GsDumpAuditReport report, bool verbose)
    {
        var diff = report.PixelDiff;
        var directDiff = report.DirectPixelDiff;
        var texture = report.TextureCorrelation;
        var unsupported = report.Render.UnsupportedStates.Values.Sum();
        var diffText = diff == null
            ? "no PNG"
            : $"MAE={diff.MeanAbsoluteError:F2}, RMSE={diff.RootMeanSquareError:F2}, max={diff.MaxChannelDifference}";
        var directDiffText = directDiff == null
            ? null
            : $"direct MAE={directDiff.MeanAbsoluteError:F2}, RMSE={directDiff.RootMeanSquareError:F2}";
        var texText = texture == null
            ? "no TEX correlation"
            : $"{texture.ResolvedTex0:N0}/{texture.UniqueRuntimeTex0:N0} TEX0 resolved";

        AnsiConsole.MarkupLine(
            $"  [green]ok[/] {Markup.Escape(Path.GetFileName(file))}: " +
            $"{report.PacketCount:N0} packets, {report.Gif.XyzWriteCount:N0} XYZ, " +
            $"{report.Render.TrianglesDrawn:N0} tris, {report.Render.PointsDrawn:N0} pts, {report.Gif.UniqueTex0Count:N0} TEX0, " +
            $"unsupported={unsupported:N0}, {Markup.Escape(diffText)}, {Markup.Escape(texText)}");
        if (directDiffText != null)
            AnsiConsole.MarkupLine($"    {Markup.Escape(directDiffText)}");

        if (!verbose)
            return;

        foreach (var row in report.Gif.TopTex0ByXyz.Take(5))
        {
            AnsiConsole.MarkupLine(
                $"    TEX0 {Markup.Escape(row.Tex0)} PSM=0x{row.Psm:X2} {row.Width}x{row.Height} XYZ={row.XyzWrites:N0}");
        }

        if (report.Render.UnsupportedStates.Count > 0)
        {
            var top = string.Join(", ", report.Render.UnsupportedStates
                .OrderByDescending(static kv => kv.Value)
                .Take(5)
                .Select(static kv => $"{kv.Key}:{kv.Value:N0}"));
            AnsiConsole.MarkupLine($"    unsupported: {Markup.Escape(top)}");
        }

        if (report.Render.PresentedFramebufferKey != null)
        {
            AnsiConsole.MarkupLine(
                $"    display: {Markup.Escape(report.Render.PresentedFramebufferKey)} " +
                $"{report.Render.PresentedFramebufferWidth}x{report.Render.PresentedFramebufferHeight}, " +
                $"nonblack={report.Render.PresentedFramebufferNonBlackPixels:N0}");
        }

        if (report.Render.PresentationFitApplied &&
            report.Render.PresentationReferenceBounds != null)
        {
            var bounds = report.Render.PresentationReferenceBounds;
            AnsiConsole.MarkupLine(
                $"    presentation fit: x={bounds.X}, y={bounds.Y}, " +
                $"{bounds.Width}x{bounds.Height}");
        }

        if (report.Render.DepthRejectedPixels > 0 || report.Render.AlphaFailedPixels > 0)
        {
            AnsiConsole.MarkupLine(
                $"    tests: depth-reject={report.Render.DepthRejectedPixels:N0}, " +
                $"alpha-fail={report.Render.AlphaFailedPixels:N0} " +
                $"(fb-only={report.Render.AlphaFailFramebufferOnlyPixels:N0}, " +
                $"zb-only={report.Render.AlphaFailZBufferOnlyPixels:N0}, " +
                $"rgb-only={report.Render.AlphaFailRgbOnlyPixels:N0})");
        }

        if (report.Render.FixedTexturePixels > 0 || report.Render.PerspectiveTexturePixels > 0)
        {
            AnsiConsole.MarkupLine(
                $"    texture coords: fixed={report.Render.FixedTexturePixels:N0}px, " +
                $"stq={report.Render.PerspectiveTexturePixels:N0}px");
        }

        if (report.Render.MissingTextureDraws.Count > 0)
        {
            var top = report.Render.MissingTextureDraws[0];
            AnsiConsole.MarkupLine(
                $"    top missing texture: {Markup.Escape(top.Tex0)} PSM=0x{top.Psm:X2} " +
                $"{top.Width}x{top.Height} draws={top.Draws:N0} target={Markup.Escape(top.FramebufferKey)}");
        }

        if (report.Render.AlphaFailureDraws.Count > 0)
        {
            var top = report.Render.AlphaFailureDraws[0];
            var bounds = top.Bounds == null
                ? ""
                : $" bounds={top.Bounds.X},{top.Bounds.Y} {top.Bounds.Width}x{top.Bounds.Height}";
            AnsiConsole.MarkupLine(
                $"    top alpha reject: {Markup.Escape(top.Tex0)} PSM=0x{top.Psm:X2} " +
                $"{top.Width}x{top.Height} {top.FailMode} pixels={top.Pixels:N0}" +
                $"{Markup.Escape(bounds)}");
        }

        if (report.Render.FramebufferSnapshots.Count > 0)
            AnsiConsole.MarkupLine($"    framebuffer snapshots: {report.Render.FramebufferSnapshots.Count:N0} pngs");

        if (report.Render.FramebufferTargets.Count > 0)
        {
            var top = string.Join(", ", report.Render.FramebufferTargets
                .OrderByDescending(static kv => kv.Value.PixelsWritten)
                .Take(5)
                .Select(static kv => $"{kv.Key}:{kv.Value.PixelsWritten:N0}px/{kv.Value.Draws:N0}draw"));
            AnsiConsole.MarkupLine($"    framebuffer writes: {Markup.Escape(top)}");
        }
    }
}
