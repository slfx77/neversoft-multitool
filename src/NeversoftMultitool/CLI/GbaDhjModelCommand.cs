using System.CommandLine;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>Exports Visual Impact's posed Downhill Jam GBA rider meshes to GLB.</summary>
public static class GbaDhjModelCommand
{
    // This clip was retained by the live frame-4800 gameplay object used to
    // close the model and animation layouts.  It is a useful stable standing/
    // skating pose, while --clip/--frame expose every other bounded pose.
    private const int RuntimeVerifiedClipIndex = 79;

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a Tony Hawk's Downhill Jam GBA ROM"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for the rider GLBs"
        };
        var indexOption = new Option<int?>("--index")
        {
            Description = "Export only one zero-based rider/model variant"
        };
        var clipOption = new Option<int>("--clip")
        {
            Description = "Pose clip index from the ROM animation directory",
            DefaultValueFactory = _ => RuntimeVerifiedClipIndex
        };
        var frameOption = new Option<int>("--frame")
        {
            Description = "Zero-based pose frame within --clip",
            DefaultValueFactory = _ => 0
        };
        var animateOption = new Option<bool>("--animate")
        {
            Description =
                "Export the whole --clip as morph-target animation instead of one pose"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "List each mesh and its source banks"
        };

        var command = new Command(
            "gba-dhj-model",
            "Export Downhill Jam GBA's pose-assembled 3D rider meshes to GLB");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(indexOption);
        command.Options.Add(clipOption);
        command.Options.Add(frameOption);
        command.Options.Add(animateOption);
        command.Options.Add(verboseOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(indexOption),
                parseResult.GetValue(clipOption),
                parseResult.GetValue(frameOption),
                parseResult.GetValue(animateOption),
                parseResult.GetValue(verboseOption),
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input,
        string? outputDir,
        int? selectedIndex,
        int clipIndex,
        int frameIndex,
        bool animate = false,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(input);
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var models = GbaDhjModel.FindModels(rom);
        if (models.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Downhill Jam rider model bank found[/] in this ROM");
            return 0;
        }

        var poseLibraries = GbaDhjModel.FindPoseLibraries(rom);
        if (Validate(models, poseLibraries, selectedIndex, clipIndex, frameIndex, animate) is { } problem)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(problem)}");
            return 1;
        }

        var poseLibrary = poseLibraries[0];
        var pose = GbaDhjModel.ReadPoseFrame(rom, poseLibrary, clipIndex, frameIndex);

        var dir = outputDir
                  ?? Path.Combine("TestOutput", Path.GetFileNameWithoutExtension(input) + "-gba-dhj-models");
        var selection = selectedIndex.HasValue
            ? [models[selectedIndex.Value]]
            : models;
        var written = 0;
        foreach (var model in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = $"rider_{model.Index:D2}";
            var document = BuildDocument(rom, model, poseLibrary, pose, clipIndex, animate, name);
            if (document == null)
            {
                // Fail closed. --animate is refused rather than quietly answered
                // with the single-pose export, which would present an unbounded or
                // otherwise unusable clip as a decoded animation.
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Pose clip {clipIndex} cannot be exported as animation");
                return 1;
            }

            var result = ModelExportService.Export(document, new MeshExportRequest
            {
                OutputDirectory = dir,
                OutputStem = name,
                Format = MeshOutputFormat.Glb,
                CancellationToken = cancellationToken
            });
            if (result.OutputPaths.Count == 0)
                continue;

            written++;
            if (verbose)
                ReportModel(model, name, document, pose, animate);
        }

        ReportSummary(written, dir, poseLibrary, clipIndex, frameIndex, animate);
        return 0;
    }

    /// <summary>
    ///     The first problem with a requested selection, or null when it can be
    ///     exported. Kept separate so every refusal reads in one place and so the
    ///     export loop never has to decide whether a request was valid.
    /// </summary>
    private static string? Validate(
        IReadOnlyList<GbaDhjModel.ModelInfo> models,
        IReadOnlyList<GbaDhjModel.PoseLibraryInfo> poseLibraries,
        int? selectedIndex,
        int clipIndex,
        int frameIndex,
        bool animate)
    {
        if (selectedIndex is < 0 || selectedIndex >= models.Count)
            return $"Model index must be between 0 and {models.Count - 1}";
        if (poseLibraries.Count != 1)
            return $"Expected one Downhill Jam pose directory, found {poseLibraries.Count}";

        var library = poseLibraries[0];
        if (clipIndex < 0 || clipIndex >= library.ClipCount)
            return $"Pose clip must be between 0 and {library.ClipCount - 1}";

        // The directory's final clip has no following offset to bound it, so its
        // frame count stays -1 rather than being guessed; it is refused for both
        // the single-pose and the animated route.
        var frameCount = library.ClipFrameCounts[clipIndex];
        if (frameCount < 0)
            return "The final pose clip is unbounded; choose a preceding clip";

        // --animate always starts at the clip's own frame 0, because that frame is
        // the base mesh every morph target is a delta from. Silently ignoring an
        // explicit --frame would export something other than what was asked for.
        if (animate && frameIndex != 0)
            return "--frame selects a single pose and cannot be combined with --animate";

        return frameIndex < 0 || frameIndex >= frameCount
            ? $"Pose frame must be between 0 and {frameCount - 1} for clip {clipIndex}"
            : null;
    }

    private static ModelDocument? BuildDocument(
        ReadOnlySpan<byte> rom,
        GbaDhjModel.ModelInfo model,
        GbaDhjModel.PoseLibraryInfo poseLibrary,
        GbaDhjModel.PoseFrame pose,
        int clipIndex,
        bool animate,
        string name) =>
        animate
            ? GbaDhjAnimatedModelWriter.TryBuild(rom, model, poseLibrary, clipIndex, name)
            : GbaDhjModelGeometryWriter.Build(rom, model, pose, name);

    private static void ReportModel(
        GbaDhjModel.ModelInfo model,
        string name,
        ModelDocument document,
        GbaDhjModel.PoseFrame pose,
        bool animate)
    {
        var channel = document.Animations.FirstOrDefault()?.MorphChannel;
        var tail = animate
            ? $"keys {channel?.KeyCount ?? 0}  morph targets {channel?.TargetCount ?? 0}"
            : $"pose 0x{0x08000000 + pose.Offset:X8}";
        AnsiConsole.MarkupLine(
            $"  {name}.glb  header 0x{0x08000000 + model.HeaderOffset:X8}  "
            + $"vertices {model.VertexCount} @ 0x{0x08000000 + model.VertexDataOffset:X8}  "
            + $"faces {model.FaceCount} @ 0x{0x08000000 + model.FaceDataOffset:X8}  "
            + tail);
    }

    private static void ReportSummary(
        int written,
        string dir,
        GbaDhjModel.PoseLibraryInfo poseLibrary,
        int clipIndex,
        int frameIndex,
        bool animate)
    {
        AnsiConsole.MarkupLine(
            $"Exported [green]{written}[/] pose-assembled Downhill Jam rider meshes to "
            + $"[green]{Markup.Escape(dir)}[/]");
        var applied = animate
            ? $"Applied all {poseLibrary.ClipFrameCounts[clipIndex]} pose records of clip {clipIndex} "
              + $"as morph targets at an exported {GbaDhjAnimatedModelWriter.PoseRecordsPerSecond:0} "
              + "records/second (an export policy: the retained trace shows the engine consuming one "
              + "record every 2-3 video frames, so real playback is slower and its rule is not decoded)."
            : $"Applied clip {clipIndex}, frame {frameIndex} with the engine's 13-part transform.";
        AnsiConsole.MarkupLine(
            $"[grey]{Markup.Escape(applied)} "
            + "Group colours are diagnostic; the game's palette/ramp binding is not decoded.[/]");
    }
}
