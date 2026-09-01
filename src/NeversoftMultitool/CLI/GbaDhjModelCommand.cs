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
        bool verbose,
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

        if (selectedIndex is < 0 || selectedIndex >= models.Count)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Model index must be between 0 and {models.Count - 1}");
            return 1;
        }

        var poseLibraries = GbaDhjModel.FindPoseLibraries(rom);
        if (poseLibraries.Count != 1)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Expected one Downhill Jam pose directory, found {poseLibraries.Count}");
            return 1;
        }

        var poseLibrary = poseLibraries[0];
        if (clipIndex < 0 || clipIndex >= poseLibrary.ClipCount)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Pose clip must be between 0 and {poseLibrary.ClipCount - 1}");
            return 1;
        }

        var frameCount = poseLibrary.ClipFrameCounts[clipIndex];
        if (frameCount < 0)
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] The final pose clip is unbounded; choose a preceding clip");
            return 1;
        }

        if (frameIndex < 0 || frameIndex >= frameCount)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Pose frame must be between 0 and {frameCount - 1} for clip {clipIndex}");
            return 1;
        }

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
            var document = GbaDhjModelGeometryWriter.Build(rom, model, pose, name);
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
            {
                AnsiConsole.MarkupLine(
                    $"  {name}.glb  header 0x{0x08000000 + model.HeaderOffset:X8}  "
                    + $"vertices {model.VertexCount} @ 0x{0x08000000 + model.VertexDataOffset:X8}  "
                    + $"faces {model.FaceCount} @ 0x{0x08000000 + model.FaceDataOffset:X8}  "
                    + $"pose 0x{0x08000000 + pose.Offset:X8}");
            }
        }

        AnsiConsole.MarkupLine(
            $"Exported [green]{written}[/] pose-assembled Downhill Jam rider meshes to "
            + $"[green]{Markup.Escape(dir)}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]Applied clip {clipIndex}, frame {frameIndex} with the engine's 13-part transform. "
            + "Group colours are diagnostic; the game's palette/ramp binding is not decoded.[/]");
        return 0;
    }
}
