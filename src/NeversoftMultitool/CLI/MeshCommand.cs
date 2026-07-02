using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class MeshCommand
{
    private static readonly string[] XbxSceneSuffixes = [".skin.xbx", ".mdl.xbx", ".skin.wpc", ".mdl.wpc"];
    private static readonly string[] Ps2SceneSuffixes = [".skin.ps2", ".mdl.ps2", ".iskin.ps2"];
    private static readonly string[] CollisionSuffixes = [".col.xbx", ".col.wpc", ".col.ps2", ".col.psp"];
    private static readonly string[] AmbiguousSceneSuffixes = [".skin", ".mdl"];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a supported mesh file or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for exported mesh files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var texPathOption = new Option<string?>("--tex")
        {
            Description = "Explicit texture file or directory to use for texture lookup"
        };
        var skeletonOption = new Option<string?>("--ske")
        {
            Description = "Skeleton file or directory for PS2 skin files"
        };
        var ddxOption = new Option<string?>("--ddx")
        {
            Description = "DDX texture archive directory for DDM files"
        };
        var psxOption = new Option<string?>("--psx")
        {
            Description = "PSX layout file or directory for placed DDM assembly"
        };
        var ddmTexturesOption = new Option<string?>("-t", "--textures")
        {
            Description = "Directory with extracted DDX texture PNGs for DDM files"
        };
        var scaleOption = new Option<float>("--scale", "--coordinate-scale", "--worldzone-scale")
        {
            Description = "Multiply exported coordinates by this positive scale for formats that support it.",
            DefaultValueFactory = _ => 1f
        };
        var worldzoneTimeOfDayOption = new Option<string>("--worldzone-time-of-day")
        {
            Description = "For THAW PS2 worldzones, choose which time-of-day layers to export: all, day, or night.",
            DefaultValueFactory = _ => "all"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("mesh",
            "Auto-detect and convert supported mesh files to glTF (.glb) or Blender (.blend)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texPathOption);
        command.Options.Add(skeletonOption);
        command.Options.Add(ddxOption);
        command.Options.Add(psxOption);
        command.Options.Add(ddmTexturesOption);
        command.Options.Add(scaleOption);
        command.Options.Add(worldzoneTimeOfDayOption);
        command.Options.Add(verboseOption);
        command.Options.Add(formatOption);
        command.Options.Add(blenderHelperOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var texPath = parseResult.GetValue(texPathOption);
            var skeletonPath = parseResult.GetValue(skeletonOption);
            var ddxPath = parseResult.GetValue(ddxOption);
            var psxPath = parseResult.GetValue(psxOption);
            var ddmTexturePath = parseResult.GetValue(ddmTexturesOption);
            var scale = parseResult.GetValue(scaleOption);
            var verbose = parseResult.GetValue(verboseOption);
            if (!MeshExportCliOptions.ValidateFormat(parseResult.GetValue(formatOption), out var format))
                return Task.FromResult(1);
            var blenderHelperPath = parseResult.GetValue(blenderHelperOption);

            if (!float.IsFinite(scale) || scale <= 0f)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --scale must be a finite positive number");
                return Task.FromResult(1);
            }

            if (!TryParseWorldzoneTimeOfDay(
                    parseResult.GetValue(worldzoneTimeOfDayOption),
                    out var worldzoneTimeOfDay))
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] --worldzone-time-of-day must be one of: all, day, night");
                return Task.FromResult(1);
            }

            return Task.FromResult(Execute(
                input,
                output,
                texPath,
                skeletonPath,
                ddxPath,
                psxPath,
                ddmTexturePath,
                scale,
                worldzoneTimeOfDay,
                verbose,
                format,
                blenderHelperPath,
                cancellationToken));
        });

        return command;
    }

    private static int Execute(
        string input,
        string output,
        string? texturePath,
        string? skeletonPath,
        string? ddxPath,
        string? psxPath,
        string? ddmTexturePath,
        float coordinateScale,
        WorldzoneTimeOfDay worldzoneTimeOfDay,
        bool verbose,
        MeshOutputFormat format,
        string? blenderHelperPath,
        CancellationToken cancellationToken)
    {
        var files = CollectInputFiles(input);
        if (files == null)
            return 1;

        var candidates = new List<MeshCandidate>();
        var skipped = new List<(string File, string Reason)>();
        foreach (var file in files)
        {
            if (TryDetect(file, psxPath, out var candidate, out var reason))
                candidates.Add(candidate);
            else if (File.Exists(file))
                skipped.Add((file, reason ?? "Unsupported mesh format"));
        }

        if (candidates.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No supported mesh files found.[/]");
            if (files.Count == 1 && skipped.Count == 1)
                AnsiConsole.MarkupLine(
                    $"  {Markup.Escape(Path.GetFileName(skipped[0].File))}: {Markup.Escape(skipped[0].Reason)}");
            return 0;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine(
            $"Found [green]{candidates.Count}[/] mesh file(s)" +
            (skipped.Count > 0 ? $" ([yellow]{skipped.Count} skipped[/])" : ""));

        if (verbose && skipped.Count > 0)
        {
            foreach (var (file, reason) in skipped)
                AnsiConsole.MarkupLine(
                    $"  [yellow]skip[/] {Markup.Escape(Path.GetFileName(file))}: {Markup.Escape(reason)}");
        }

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var totalTriangles = 0;

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var result = MeshExportCliOptions.ExportFile(
                    candidate.File,
                    output,
                    candidate.SourceKind,
                    format,
                    blenderHelperPath,
                    cancellationToken,
                    candidate.OutputStem,
                    candidate.Ps2SubFormat,
                    candidate.HasPlacedPsxCompanion,
                    texturePath,
                    skeletonPath,
                    ddxPath,
                    psxPath,
                    ddmTexturePath,
                    worldzoneTimeOfDay,
                    coordinateScale);

                converted++;
                totalTriangles += result.Triangles;
                if (verbose)
                {
                    var paths = result.OutputPaths.Count > 0
                        ? string.Join(", ", result.OutputPaths.Select(Path.GetFileName))
                        : "no output";
                    AnsiConsole.MarkupLine(
                        $"  [green]ok[/] {Markup.Escape(Path.GetFileName(candidate.File))} " +
                        $"({Markup.Escape(candidate.DisplayFormat)}): {result.Triangles:N0} triangles -> {Markup.Escape(paths)}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                AnsiConsole.MarkupLine(
                    $"  [red]error[/] {Markup.Escape(Path.GetFileName(candidate.File))}: {Markup.Escape(ex.Message)}");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{candidates.Count} files " +
            $"({totalTriangles:N0} triangles) in {stopwatch.Elapsed.TotalSeconds:F1}s" +
            (failed > 0 ? $", [red]{failed} failed[/]" : ""));
        return failed > 0 ? 1 : 0;
    }

    private static List<string>? CollectInputFiles(string input)
    {
        if (File.Exists(input))
            return [input];

        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return null;
        }

        return Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
            .Where(IsPotentialMeshFile)
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPotentialMeshFile(string path)
    {
        var name = Path.GetFileName(path);
        return OrdinalFileName.HasAnySuffix(name, XbxSceneSuffixes)
               || OrdinalFileName.HasAnySuffix(name, Ps2SceneSuffixes)
               || OrdinalFileName.HasAnySuffix(name, CollisionSuffixes)
               || OrdinalFileName.HasAnySuffix(name, AmbiguousSceneSuffixes)
               || OrdinalFileName.HasSuffix(name, ".geom.ps2")
               || OrdinalFileName.HasSuffix(name, ".pak.ps2")
               || OrdinalFileName.HasSuffix(name, ".ddm")
               || OrdinalFileName.HasSuffix(name, ".psx")
               || OrdinalFileName.HasSuffix(name, ".skn")
               || OrdinalFileName.HasSuffix(name, ".bsp");
    }

    private static bool TryDetect(
        string file,
        string? psxPath,
        out MeshCandidate candidate,
        out string? reason)
    {
        candidate = default;
        reason = null;
        var name = Path.GetFileName(file);

        if (OrdinalFileName.HasSuffix(name, ".pak.ps2"))
        {
            if (!Ps2WorldzoneDetection.IsWorldzonePak(file))
            {
                reason = "Not a recognized THAW PS2 worldzone PAK";
                return false;
            }

            candidate = new MeshCandidate(
                file,
                ModelSourceKind.Ps2Worldzone,
                MeshExportCliOptions.StripKnownExtension(file, [".pak.ps2"]),
                "THAW PS2 Worldzone",
                Ps2SceneSubFormat.PakWorldzone);
            return true;
        }

        if (OrdinalFileName.HasAnySuffix(name, XbxSceneSuffixes))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.XbxScene,
                MeshExportCliOptions.StripKnownExtension(file, XbxSceneSuffixes),
                name.EndsWith(".wpc", StringComparison.OrdinalIgnoreCase) ? "PC Scene" : "Xbox Scene");
            return true;
        }

        if (OrdinalFileName.HasAnySuffix(name, Ps2SceneSuffixes))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.Ps2Scene,
                MeshExportCliOptions.StripKnownExtension(file, Ps2SceneFile.SupportedExtensions),
                "PS2 Scene",
                MeshExportCliOptions.DetectPs2SceneSubFormat(file));
            return true;
        }

        if (OrdinalFileName.HasSuffix(name, ".geom.ps2"))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.Ps2Geom,
                MeshExportCliOptions.StripKnownExtension(file, [".geom.ps2"]),
                "PS2 GEOM",
                Ps2SceneSubFormat.Geom);
            return true;
        }

        if (OrdinalFileName.HasAnySuffix(name, CollisionSuffixes))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.Collision,
                MeshExportCliOptions.StripColExtension(file),
                "Collision");
            return true;
        }

        if (OrdinalFileName.HasSuffix(name, ".ddm"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.Ddm,
                stem,
                "DDM Mesh",
                HasPlacedPsxCompanion: psxPath != null || HasSibling(file, stem, ".psx"));
            return true;
        }

        if (OrdinalFileName.HasSuffix(name, ".psx"))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.Psx,
                Path.GetFileNameWithoutExtension(file),
                "PSX Mesh");
            return true;
        }

        if (OrdinalFileName.HasSuffix(name, ".skn"))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.RenderWareDff,
                Path.GetFileNameWithoutExtension(file),
                "RenderWare DFF");
            return true;
        }

        if (OrdinalFileName.HasSuffix(name, ".bsp"))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.RenderWareBsp,
                Path.GetFileNameWithoutExtension(file),
                "RenderWare BSP");
            return true;
        }

        if (OrdinalFileName.HasAnySuffix(name, AmbiguousSceneSuffixes))
            return TryDetectAmbiguousScene(file, out candidate, out reason);

        reason = "Unrecognized mesh extension";
        return false;
    }

    private static bool TryDetectAmbiguousScene(
        string file,
        out MeshCandidate candidate,
        out string? reason)
    {
        candidate = default;
        reason = null;
        var data = File.ReadAllBytes(file);
        var name = Path.GetFileName(file);
        var stem = Path.GetFileNameWithoutExtension(file);

        if (ThawSceneFile.IsThawScene(data) || XbxSceneFile.IsXbxScene(data))
        {
            candidate = new MeshCandidate(file, ModelSourceKind.XbxScene, stem, "PC/Xbox Scene");
            return true;
        }

        if (OrdinalFileName.HasSuffix(name, ".mdl") && Ps2GeomFile.IsPakMdl(data))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.Ps2Scene,
                stem,
                "PAK MDL (THAW PS2)",
                Ps2SceneSubFormat.PakMdl);
            return true;
        }

        if (OrdinalFileName.HasSuffix(name, ".skin") && ThawPs2SkinFile.IsPakSkin(data))
        {
            candidate = new MeshCandidate(
                file,
                ModelSourceKind.Ps2Scene,
                stem,
                "PAK Skin (THAW PS2)",
                Ps2SceneSubFormat.PakSkin);
            return true;
        }

        reason = "Ambiguous .skin/.mdl file is not a recognized PC/Xbox scene or THAW PS2 PAK mesh";
        return false;
    }

    private static bool HasSibling(string file, string stem, string extension)
    {
        var dir = Path.GetDirectoryName(file);
        return dir != null && File.Exists(Path.Combine(dir, stem + extension));
    }

    private static bool TryParseWorldzoneTimeOfDay(
        string? value,
        out WorldzoneTimeOfDay timeOfDay)
    {
        switch ((value ?? "all").Trim().ToLowerInvariant())
        {
            case "all":
                timeOfDay = WorldzoneTimeOfDay.All;
                return true;
            case "day":
                timeOfDay = WorldzoneTimeOfDay.Day;
                return true;
            case "night":
                timeOfDay = WorldzoneTimeOfDay.Night;
                return true;
            default:
                timeOfDay = default;
                return false;
        }
    }

    private readonly record struct MeshCandidate(
        string File,
        ModelSourceKind SourceKind,
        string OutputStem,
        string DisplayFormat,
        Ps2SceneSubFormat Ps2SubFormat = Ps2SceneSubFormat.None,
        bool HasPlacedPsxCompanion = false);
}
