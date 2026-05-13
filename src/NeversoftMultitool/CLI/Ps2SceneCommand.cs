using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class Ps2SceneCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PS2 scene file (.mdl.ps2, .skin.ps2, .iskin.ps2) or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted mesh files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var texPathOption = new Option<string?>("--tex")
        {
            Description = "Explicit TEX file or directory to use for texture lookup"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var skeletonOption = new Option<string?>("--ske")
        {
            Description =
                "Skeleton file (.ske.ps2 or .ske) or directory. Auto-discovered for .skin.ps2 files if not specified."
        };
        var worldzoneOption = new Option<bool>("--worldzone")
        {
            Description =
                "Treat the input .pak.ps2 as a THAW worldzone and place every .mdl entry at the positions recovered from its paired .91E1028D placement entry."
        };
        var worldzoneCombinedOption = new Option<bool>("--worldzone-combined")
        {
            Description =
                "Compatibility flag. THAW worldzones are always emitted as one combined ModelDocument."
        };
        var worldzoneDebugTexturesOption = new Option<bool>("--worldzone-debug-textures")
        {
            Description =
                "Legacy worldzone debug artifact flag. Not available in the ModelDocument-only worldzone path."
        };
        var worldzoneDebugLeafColorsOption = new Option<bool>("--worldzone-debug-leaf-colors")
        {
            Description =
                "Legacy worldzone debug leaf-color flag. Not available in the ModelDocument-only worldzone path."
        };
        var worldzoneTimeOfDayOption = new Option<string>("--worldzone-time-of-day")
        {
            Description =
                "When --worldzone is set, choose which time-of-day layers to export: all, day, or night.",
            DefaultValueFactory = _ => "all"
        };
        var worldzoneScaleOption = new Option<float>("--worldzone-scale")
        {
            Description =
                "When --worldzone is set, multiply exported coordinates by this positive scale. Use 0.01 for Blender-friendly viewing while preserving relative layout.",
            DefaultValueFactory = _ => 1f
        };
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("ps2scene", "Convert PS2 scene files (MDL/SKIN) to glTF (.glb) or Blender (.blend)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texPathOption);
        command.Options.Add(verboseOption);
        command.Options.Add(skeletonOption);
        command.Options.Add(worldzoneOption);
        command.Options.Add(worldzoneCombinedOption);
        command.Options.Add(worldzoneDebugTexturesOption);
        command.Options.Add(worldzoneDebugLeafColorsOption);
        command.Options.Add(worldzoneTimeOfDayOption);
        command.Options.Add(worldzoneScaleOption);
        command.Options.Add(formatOption);
        command.Options.Add(blenderHelperOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var texPath = parseResult.GetValue(texPathOption);
            var verbose = parseResult.GetValue(verboseOption);
            var skePath = parseResult.GetValue(skeletonOption);
            var worldzone = parseResult.GetValue(worldzoneOption);
            var worldzoneDebugTextures = parseResult.GetValue(worldzoneDebugTexturesOption);
            var worldzoneDebugLeafColors = parseResult.GetValue(worldzoneDebugLeafColorsOption);
            var worldzoneTimeOfDayText = parseResult.GetValue(worldzoneTimeOfDayOption);
            var worldzoneScale = parseResult.GetValue(worldzoneScaleOption);
            if (!MeshExportCliOptions.ValidateFormat(parseResult.GetValue(formatOption), out var format))
                return Task.FromResult(1);
            var blenderHelper = parseResult.GetValue(blenderHelperOption);

            if (worldzone)
            {
                if (!TryParseWorldzoneTimeOfDay(worldzoneTimeOfDayText, out var worldzoneTimeOfDay))
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] --worldzone-time-of-day must be one of: all, day, night");
                    return Task.FromResult(1);
                }

                if (!float.IsFinite(worldzoneScale) || worldzoneScale <= 0f)
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] --worldzone-scale must be a finite positive number");
                    return Task.FromResult(1);
                }

                if (worldzoneDebugTextures || worldzoneDebugLeafColors)
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] --worldzone-debug-textures and --worldzone-debug-leaf-colors " +
                        "belonged to the legacy worldzone exporter. THAW worldzones now always use " +
                        "the ModelDocument parser; use --format blend for leaf/material inspection.");
                    return Task.FromResult(1);
                }

                return Task.FromResult(ExecuteWorldzone(
                    input,
                    output,
                    texPath,
                    format,
                    blenderHelper,
                    worldzoneTimeOfDay,
                    worldzoneScale,
                    verbose,
                    cancellationToken));
            }

            return Task.FromResult(Execute(input, output, texPath, verbose, skePath, format, blenderHelper,
                cancellationToken));
        });

        return command;
    }

    private static int ExecuteWorldzone(
        string input,
        string output,
        string? texPath,
        MeshOutputFormat format,
        string? blenderHelperPath,
        Ps2WorldzoneConverter.WorldzoneTimeOfDay timeOfDay,
        float coordinateScale,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --worldzone requires an existing .pak.ps2 file: {input}");
            return 1;
        }

        if (!Ps2WorldzoneConverter.IsWorldzonePak(input))
        {
            AnsiConsole.MarkupLine($"[yellow]No .mdl entries found in {input}.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"Worldzone [green]{Path.GetFileName(input)}[/]: " +
            $"ModelDocument export, format: [green]{format.ToString().ToLowerInvariant()}[/], " +
            $"time-of-day: [green]{timeOfDay.ToString().ToLowerInvariant()}[/], " +
            $"scale: [green]{coordinateScale:G}[/]");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = MeshExportCliOptions.ExportFile(
                input,
                output,
                ModelSourceKind.Ps2Worldzone,
                format,
                blenderHelperPath,
                cancellationToken,
                MeshExportCliOptions.StripKnownExtension(input, [".pak.ps2"]),
                Ps2SceneSubFormat.PakWorldzone,
                texturePath: texPath,
                worldzoneTimeOfDay: timeOfDay,
                worldzoneScale: coordinateScale);

            stopwatch.Stop();

            if (verbose && result.OutputPaths.Count > 0)
            {
                foreach (var path in result.OutputPaths)
                    AnsiConsole.MarkupLine($"  wrote [green]{Markup.Escape(path)}[/]");
            }

            AnsiConsole.MarkupLine(
                $"Worldzone: [green]{result.Triangles:N0}[/] triangles, " +
                $"[green]{result.MaterialCount:N0}[/] materials, " +
                $"[green]{result.TextureCount:N0}[/] textures " +
                $"in {stopwatch.Elapsed.TotalSeconds:F2}s");
            return 0;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static bool TryParseWorldzoneTimeOfDay(
        string? value,
        out Ps2WorldzoneConverter.WorldzoneTimeOfDay timeOfDay)
    {
        switch ((value ?? "all").Trim().ToLowerInvariant())
        {
            case "all":
                timeOfDay = Ps2WorldzoneConverter.WorldzoneTimeOfDay.All;
                return true;
            case "day":
                timeOfDay = Ps2WorldzoneConverter.WorldzoneTimeOfDay.Day;
                return true;
            case "night":
                timeOfDay = Ps2WorldzoneConverter.WorldzoneTimeOfDay.Night;
                return true;
            default:
                timeOfDay = default;
                return false;
        }
    }

    private static int Execute(string input, string output,
        string? texPath, bool verbose, string? skePath, MeshOutputFormat format,
        string? blenderHelperPath, CancellationToken cancellationToken)
    {
        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(IsPs2SceneFile)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No PS2 scene files found.[/]");
            return 0;
        }

        // Probe for unsupported files (THAW .skin.ps2, Xbox/PC scene files)
        var (supported, unsupported) = FormatProbe.PartitionFiles(files, FormatProbe.ProbeMesh);
        if (unsupported.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"Found [green]{files.Count}[/] files " +
                $"([green]{supported.Count}[/] supported, [yellow]{unsupported.Count}[/] unsupported)");
            foreach (var (fileName, reason) in unsupported)
                AnsiConsole.MarkupLine($"  [yellow]\u26a0[/] {Markup.Escape(fileName)}: {Markup.Escape(reason)}");
            files = supported;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No supported PS2 scene files to process.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] PS2 scene file(s)");
        return MeshExportCliOptions.ExportFiles(
            files,
            output,
            ModelSourceKind.Ps2Scene,
            format,
            blenderHelperPath,
            verbose,
            cancellationToken,
            file => MeshExportCliOptions.StripKnownExtension(file, Ps2SceneFile.SupportedExtensions),
            MeshExportCliOptions.DetectPs2SceneSubFormat,
            texturePath: texPath,
            skeletonPath: skePath);
    }

    private static bool IsPs2SceneFile(string path)
    {
        var name = Path.GetFileName(path);
        return Ps2SceneFile.SupportedExtensions
            .Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
