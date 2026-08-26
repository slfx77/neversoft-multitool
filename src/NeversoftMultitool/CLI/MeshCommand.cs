using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class MeshCommand
{
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
            Description =
                "Skeleton file or directory for PS2, Xbox, PC, and GameCube skin weights " +
                "when compatible"
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
        var worldzoneDebugDirOption = new Option<string?>("--worldzone-debug-dir")
        {
            Description =
                "For THAW PS2 worldzones, write triage diagnostics into this directory: "
                + "per-leaf rejection reasons ({stem}.rejections.csv), emitted-leaf GS state "
                + "with texture-resolution tags ({stem}.materials.csv), and the texture "
                + "catalog debug dump. Conversion output is unchanged."
        };
        var psxLightOption = new Option<string?>("--psx-light")
        {
            Description =
                "Shade PS1 engine-lit faces with a named light rig extracted from the game "
                + "binaries (item-default, skater, skater-mars). Omitted, those faces keep their "
                + "authored colours for the viewer to light — the file records WHICH faces the "
                + "engine lights but never WHICH light, so the rig cannot be inferred."
        };
        var n64AnimationsOption = new Option<bool>("--n64-animations")
        {
            Description =
                "Embed every direct 0x2A or compressed 0x2C clip for conservatively eligible "
                + "N64 models. Off by default because character banks can contain hundreds of clips "
                + "(use --n64-animation <n> to pick individual slots); direct tween endings default "
                + "to the PSX CycleAnim export policy, selectable with --one-shot, while N64 timing "
                + "and loop/clamp behavior remain unproven."
        };
        var n64AnimationOption = new Option<int[]>("--n64-animation")
        {
            Description =
                "Embed one specific N64 animation slot by index. Repeat the option to select "
                + "several. Unlike --n64-animations this does not pull in a character bank's "
                + "hundreds of clips. Out-of-range and undecodable slots are skipped.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        var oneShotOption = new Option<bool>("--one-shot")
        {
            Description =
                "Expand tween-compressed N64 clips with the RunAnim one-shot clamp (hold the last "
                + "keyframe) instead of the default CycleAnim wrap toward frame 0. Applies to N64 "
                + "DIRECT (0x2A) clips only: `mesh` emits no PSX animation tracks (use "
                + "psx-anim-export --one-shot for those), and compressed 0x2C slots store every "
                + "frame, so they have no end-of-clip branch to select."
        };
        var gbaAnimationsOption = new Option<bool>("--gba-animations")
        {
            Description =
                "Embed every non-empty GBA skater clip (THPS2 carries 217) in the exported model "
                + "as a bone-per-vertex rig with per-tick translation keys. Off by default; the "
                + "60 ticks/s cadence is an explicit export policy (GBA video runs 59.7275 Hz)."
        };
        var gbaAnimationOption = new Option<int[]>("--gba-animation")
        {
            Description =
                "Embed one specific GBA clip by index. Repeat the option to select several. "
                + "Out-of-range and authored-empty clips are skipped.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
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
        command.Options.Add(worldzoneDebugDirOption);
        command.Options.Add(psxLightOption);
        command.Options.Add(n64AnimationsOption);
        command.Options.Add(n64AnimationOption);
        command.Options.Add(oneShotOption);
        command.Options.Add(gbaAnimationsOption);
        command.Options.Add(gbaAnimationOption);
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
            var animationOptions = new MeshAnimationExportOptions(
                parseResult.GetValue(n64AnimationsOption),
                parseResult.GetValue(n64AnimationOption) is { Length: > 0 } selected
                    ? selected
                    : null,
                parseResult.GetValue(oneShotOption),
                parseResult.GetValue(gbaAnimationsOption),
                parseResult.GetValue(gbaAnimationOption) is { Length: > 0 } gbaSelected
                    ? gbaSelected
                    : null);
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

            var psxLight = parseResult.GetValue(psxLightOption);
            if (!string.IsNullOrWhiteSpace(psxLight)
                && PsxEngineLight.FromName(psxLight) == null)
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] --psx-light must be one of: "
                    + string.Join(", ", PsxEngineLight.Presets.Keys));
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
                psxLight,
                verbose,
                format,
                blenderHelperPath,
                animationOptions,
                cancellationToken,
                parseResult.GetValue(worldzoneDebugDirOption)));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        string? texturePath,
        string? skeletonPath,
        string? ddxPath,
        string? psxPath,
        string? ddmTexturePath,
        float coordinateScale,
        WorldzoneTimeOfDay worldzoneTimeOfDay,
        string? psxLightPreset,
        bool verbose,
        MeshOutputFormat format,
        string? blenderHelperPath,
        MeshAnimationExportOptions animationOptions,
        CancellationToken cancellationToken,
        string? worldzoneDebugDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isSingleFile = File.Exists(input);
        var files = CollectInputFiles(input);
        if (files == null)
            return 1;

        var candidates = new List<MeshCandidate>();
        var skipped = new List<(string File, string Reason)>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            return isSingleFile ? 1 : 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
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

        // Game trees reuse asset names heavily, so a flat output would silently
        // overwrite. Only colliding stems are relocated; everything else keeps
        // the flat layout.
        var byFile = candidates.ToDictionary(static c => c.File, StringComparer.OrdinalIgnoreCase);
        var plan = MeshOutputPathPlanner.Plan(
            [.. candidates.Select(static c => c.File)],
            file => byFile[file].OutputStem,
            Directory.Exists(input) ? input : null);
        var relocated = plan.Count(static p => p.Subdirectory.Length > 0);
        if (relocated > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{relocated}[/] file(s) share an output name; " +
                "mirroring their source folders so none are overwritten.");
        }

        foreach (var (file, subdirectory, exportStem) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = byFile[file];
            try
            {
                var result = MeshExportCliOptions.ExportFile(
                    candidate.File,
                    subdirectory.Length == 0 ? output : Path.Combine(output, subdirectory),
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
                    coordinateScale,
                    psxLightPreset,
                    exportStem,
                    animationOptions,
                    worldzoneDebugDirectory);

                if (result.OutputPaths.Count == 0)
                    throw new InvalidDataException("Mesh export produced no output.");

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
            catch (Exception ex) when (ex is not OperationCanceledException)
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
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {Markup.Escape(input)}");
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
        return MeshTypeDetector.IsMeshCandidate(name) || MeshTypeDetector.IsWorldzoneCandidate(name);
    }

    private static bool TryDetect(
        string file,
        string? psxPath,
        out MeshCandidate candidate,
        out string? reason)
    {
        candidate = default;
        var route = MeshTypeDetector.Detect(file);
        if (!route.IsSupported)
        {
            reason = route.UnsupportedReason ?? "Unrecognized mesh extension";
            return false;
        }

        reason = null;
        candidate = new MeshCandidate(
            file,
            MeshTypeDetector.ToSourceKind(route.Kind),
            OutputStemFor(file, route),
            route.DisplayFormat ?? route.Kind.ToString(),
            route.Ps2SubFormat,
            route.Kind == MeshFileKind.Ddm && HasPlacedPsxCompanion(file, psxPath));
        return true;
    }

    /// <summary>
    ///     A carved N64 bundle takes the shared N64 stem rule (slot plus the
    ///     recovered PS1 name); everything else uses the plain stem rule.
    /// </summary>
    private static string OutputStemFor(string file, in MeshFileRoute route)
    {
        return route.Kind == MeshFileKind.N64Model
            ? MeshTypeDetector.GetN64BundleStem(file)
            : MeshTypeDetector.GetStem(file);
    }

    private static bool HasPlacedPsxCompanion(string file, string? psxPath)
    {
        return psxPath != null
               || HasSibling(file, Path.GetFileNameWithoutExtension(file), ".psx");
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
