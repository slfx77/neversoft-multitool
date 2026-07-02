using System.CommandLine;
using System.Diagnostics;
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

            return Task.FromResult(Execute(input, output, size, fps, animIndex, azimuth, elevation, verbose));
        });

        return command;
    }

    private static int Execute(string input, string? output, int longEdge, int fps, int? animIndex,
        float azimuth, float elevation, bool verbose)
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

        var sw = Stopwatch.StartNew();
        var success = 0;
        var skipped = 0;
        var fail = 0;

        foreach (var file in files)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (animIndex.HasValue)
                stem += $"_anim{animIndex.Value}";
            var gifPath = output != null
                ? Path.Combine(output, stem + ".gif")
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
                        AnsiConsole.MarkupLine($"  [grey]{Path.GetFileName(file)}: no animation, skipped[/]");
                    skipped++;
                }
                else
                {
                    success++;
                    if (verbose)
                        AnsiConsole.MarkupLine(
                            $"  [green]{Path.GetFileName(file)}[/] -> [cyan]{gifPath}[/] " +
                            $"({frameCount} frames, {duration:F2}s, {fileSw.Elapsed.TotalSeconds:F1}s render)");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]FAIL[/] {Path.GetFileName(file)}: {ex.Message}");
                fail++;
            }
        }

        sw.Stop();
        AnsiConsole.MarkupLine(
            $"Done: [green]{success}[/] rendered, [grey]{skipped}[/] skipped, " +
            $"[red]{fail}[/] failed ({sw.Elapsed.TotalSeconds:F1}s)");

        return fail > 0 ? 1 : 0;
    }
}
