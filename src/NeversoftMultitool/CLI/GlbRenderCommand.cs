using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Rendering;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class GlbRenderCommand
{
    private readonly record struct RenderView(string Name, float Azimuth, float Elevation);

    private static readonly IReadOnlyList<RenderView> ObjectReviewViews =
    [
        new("front_left", -45f, 20f),
        new("front_right", 45f, 20f),
        new("rear_right", 135f, 20f),
        new("rear_left", -135f, 20f),
        new("top", -45f, 75f)
    ];

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
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var size = parseResult.GetValue(sizeOption);
            var azimuth = parseResult.GetValue(azimuthOption);
            var elevation = parseResult.GetValue(elevationOption);
            var preset = parseResult.GetValue(presetOption);
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, size, azimuth, elevation, preset, verbose));
        });

        return command;
    }

    private static int Execute(string input, string? output, int longEdge,
        float azimuth, float elevation, string? preset, bool verbose)
    {
        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*.glb", SearchOption.AllDirectories).ToList();
            AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] .glb files");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Input not found:[/] {input}");
            return 1;
        }

        if (!TryGetViews(preset, azimuth, elevation, out var views))
        {
            AnsiConsole.MarkupLine(
                $"[red]Unknown preset:[/] {Markup.Escape(preset!)} ([grey]supported: object-review[/])");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var success = 0;
        var fail = 0;
        var useViewSuffix = views.Count > 1;

        foreach (var file in files)
        {
            foreach (var view in views)
            {
                var pngPath = GetOutputPath(file, output, view, useViewSuffix);
                if (verbose)
                {
                    var angleLabel = $"az={view.Azimuth:0.##}, el={view.Elevation:0.##}";
                    AnsiConsole.MarkupLine(
                        $"Rendering [cyan]{Path.GetFileName(file)}[/] ({Markup.Escape(view.Name)}, {angleLabel}) -> [cyan]{pngPath}[/]");
                }

                try
                {
                    GlbRenderer.RenderToFile(file, pngPath, longEdge, view.Azimuth, view.Elevation);
                    success++;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]FAIL[/] {Path.GetFileName(file)} ({Markup.Escape(view.Name)}): {ex.Message}");
                    fail++;
                }
            }
        }

        sw.Stop();
        AnsiConsole.MarkupLine(
            $"Done: [green]{success}[/] rendered, [red]{fail}[/] failed ({sw.Elapsed.TotalSeconds:F1}s)");

        return fail > 0 ? 1 : 0;
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
            views = ObjectReviewViews;
            return true;
        }

        views = [];
        return false;
    }

    private static string GetOutputPath(string inputFile, string? outputDir,
        RenderView view, bool useViewSuffix)
    {
        var stem = Path.GetFileNameWithoutExtension(inputFile);
        var suffix = useViewSuffix ? "_" + view.Name : "";
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
