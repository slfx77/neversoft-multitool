using System.CommandLine;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

internal static class MeshExportCliOptions
{
    private static readonly MeshModelParser Parser = new();

    public static Option<string> CreateFormatOption()
    {
        return new Option<string>("--format")
        {
            Description = "Output mesh format: glb, blend, or both",
            DefaultValueFactory = _ => "glb"
        };
    }

    public static Option<string?> CreateBlenderHelperOption()
    {
        return new Option<string?>("--blender-helper")
        {
            Description = "Path to the Blender executable for .blend export " +
                          "(default: saved app setting, bundled helper, PATH, then standard install folders)"
        };
    }

    public static bool TryParseFormat(string? value, out MeshOutputFormat format)
    {
        switch ((value ?? "glb").Trim().ToLowerInvariant())
        {
            case "glb":
                format = MeshOutputFormat.Glb;
                return true;
            case "blend":
                format = MeshOutputFormat.Blend;
                return true;
            case "both":
                format = MeshOutputFormat.Both;
                return true;
            default:
                format = MeshOutputFormat.Glb;
                return false;
        }
    }

    public static int ExportFiles(
        IReadOnlyList<string> files,
        string output,
        ModelSourceKind sourceKind,
        MeshOutputFormat format,
        string? blenderHelperPath,
        bool verbose,
        CancellationToken cancellationToken,
        Func<string, string>? outputStem = null,
        Func<string, Ps2SceneSubFormat>? ps2SubFormat = null,
        Func<string, bool>? hasPlacedPsxCompanion = null,
        string? texturePath = null,
        string? skeletonPath = null,
        string? ddxPath = null,
        string? psxPath = null,
        string? ddmTexturePath = null,
        float worldzoneScale = 1f,
        string? inputRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(output);

        var converted = 0;
        var failed = 0;
        var totalTriangles = 0;

        var plan = MeshOutputPathPlanner.Plan(
            files,
            file => outputStem?.Invoke(file) ?? Path.GetFileNameWithoutExtension(file),
            inputRoot);
        var relocated = plan.Count(static p => p.Subdirectory.Length > 0);
        if (relocated > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{relocated}[/] file(s) share an output name; " +
                "mirroring their source folders so none are overwritten.");
        }

        foreach (var (file, subdirectory, stem) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file);
            var fileOutput = subdirectory.Length == 0 ? output : Path.Combine(output, subdirectory);
            try
            {
                var result = ExportFile(
                    file,
                    fileOutput,
                    sourceKind,
                    format,
                    blenderHelperPath,
                    cancellationToken,
                    outputStem?.Invoke(file),
                    ps2SubFormat?.Invoke(file) ?? Ps2SceneSubFormat.None,
                    hasPlacedPsxCompanion?.Invoke(file) ?? false,
                    texturePath,
                    skeletonPath,
                    ddxPath,
                    psxPath,
                    ddmTexturePath,
                    worldzoneScale: worldzoneScale,
                    exportStem: stem);

                if (result.OutputPaths.Count == 0)
                    throw new InvalidDataException("Mesh export produced no output.");

                totalTriangles += result.Triangles;
                converted++;

                if (verbose)
                {
                    var paths = result.OutputPaths.Count > 0
                        ? string.Join(", ", result.OutputPaths.Select(Path.GetFileName))
                        : "no output";
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(fileName)}: [green]{result.Triangles:N0} triangles[/] -> {Markup.Escape(paths)}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                AnsiConsole.MarkupLine(
                    $"  {Markup.Escape(fileName)}: [red]{Markup.Escape(ex.Message)}[/]");
            }
        }

        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} files " +
            $"({totalTriangles:N0} triangles)" +
            (failed > 0 ? $", [red]{failed} failed[/]" : ""));
        return failed > 0 ? 1 : 0;
    }

    public static MeshExportResult ExportFile(
        string file,
        string output,
        ModelSourceKind sourceKind,
        MeshOutputFormat format,
        string? blenderHelperPath,
        CancellationToken cancellationToken,
        string? outputStem = null,
        Ps2SceneSubFormat ps2SubFormat = Ps2SceneSubFormat.None,
        bool hasPlacedPsxCompanion = false,
        string? texturePath = null,
        string? skeletonPath = null,
        string? ddxPath = null,
        string? psxPath = null,
        string? ddmTexturePath = null,
        WorldzoneTimeOfDay worldzoneTimeOfDay = WorldzoneTimeOfDay.All,
        float worldzoneScale = 1f,
        string? psxLightPreset = null,
        string? exportStem = null,
        MeshAnimationExportOptions? animationOptions = null,
        string? worldzoneDebugDirectory = null)
    {
        animationOptions ??= MeshAnimationExportOptions.None;
        var stem = outputStem ?? Path.GetFileNameWithoutExtension(file);

        // GBA clips animate by morphing, and a glTF weights track addresses every
        // target of the mesh — so each clip is its own file rather than one
        // document carrying all 217 clips' poses at once.
        if (sourceKind == ModelSourceKind.GbaModel && GbaClipSelection(animationOptions, file) is { Count: > 0 } clips)
        {
            return ExportGbaClips(
                file, output, format, blenderHelperPath, cancellationToken, stem, exportStem, clips);
        }

        var document = Parser.Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(file),
            FileName = Path.GetFileName(file),
            OutputStem = stem,
            SourceKind = sourceKind,
            Ps2SubFormat = ps2SubFormat,
            HasPlacedPsxCompanion = hasPlacedPsxCompanion,
            TexturePath = texturePath,
            SkeletonPath = skeletonPath,
            DdxPath = ddxPath,
            PsxPath = psxPath,
            DdmTexturePath = ddmTexturePath,
            WorldzoneTimeOfDay = worldzoneTimeOfDay,
            WorldzoneScale = worldzoneScale,
            PsxLightPreset = psxLightPreset,
            IncludeAllN64Animations = animationOptions.IncludeAllN64Animations,
            N64AnimationIndices = animationOptions.N64AnimationIndices,
            N64AnimationOneShot = animationOptions.OneShot,
            IncludeAllGbaAnimations = animationOptions.IncludeAllGbaAnimations,
            GbaAnimationIndices = animationOptions.GbaAnimationIndices,
            WorldzoneDebugDirectory = worldzoneDebugDirectory
        });

        return ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = output,
                // The import stem stays the ORIGINAL name because it is the
                // companion-lookup key (stem + ".ddx"/".lit"/"_o.ddm"/".ske.ps2").
                // Only the written file may be renamed to break a collision.
                OutputStem = exportStem ?? document.Name,
                Format = format,
                BlenderHelperPath = blenderHelperPath,
                WorldzoneTimeOfDay = worldzoneTimeOfDay,
                WorldzoneScale = worldzoneScale,
                CancellationToken = cancellationToken
            });
    }

    /// <summary>
    ///     The GBA clips this run should export: every non-empty clip for
    ///     <c>--gba-animations</c>, or exactly the requested indices. Empty when
    ///     no animation was asked for, so the ordinary static path runs.
    /// </summary>
    private static IReadOnlyList<int> GbaClipSelection(MeshAnimationExportOptions options, string file)
    {
        if (!options.IncludeAllGbaAnimations && options.GbaAnimationIndices is not { Count: > 0 })
            return [];

        var rom = new FileSystemAssetSource(file).TryReadCompanion(GbaLevelCarver.RomEntryName);
        var clips = rom == null ? null : GbaRiderClips.TryList(rom);
        if (clips == null)
            return [];

        // Only clips that really exist and really play: an out-of-range or
        // authored-empty index falls through to the ordinary static export
        // rather than producing a file named after a clip that has no frames.
        var playable = clips
            .Where(static clip => clip.TickCount > 0)
            .Select(static clip => clip.Index)
            .ToHashSet();
        if (options.IncludeAllGbaAnimations)
            return [.. playable.Order()];
        return [.. options.GbaAnimationIndices!.Distinct().Where(playable.Contains)];
    }

    /// <summary>
    ///     Exports one file per clip, named <c>&lt;stem&gt;__&lt;clip&gt;</c>. A clip that
    ///     does not export is skipped rather than failing the run or emitting a
    ///     static copy under an animated name.
    /// </summary>
    private static MeshExportResult ExportGbaClips(
        string file, string output, MeshOutputFormat format, string? blenderHelperPath,
        CancellationToken cancellationToken, string stem, string? exportStem, IReadOnlyList<int> clips)
    {
        var paths = new List<string>();
        var triangles = 0;
        var materials = 0;
        var textures = 0;
        foreach (var clip in clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = Parser.Parse(new MeshImportRequest
            {
                Source = new FileSystemAssetSource(file),
                FileName = Path.GetFileName(file),
                OutputStem = stem,
                SourceKind = ModelSourceKind.GbaModel,
                GbaAnimationIndices = [clip]
            });
            // A clip that never leaves the base pose has no animation to carry;
            // it still exports, as the static model in that pose.
            var clipName = document.Animations.Count > 0
                ? document.Animations[0].Name
                : GbaClipName(file, clip);

            var result = ModelExportService.Export(document, new MeshExportRequest
            {
                OutputDirectory = output,
                OutputStem = $"{exportStem ?? document.Name}__{SafeClipName(clipName)}",
                Format = format,
                BlenderHelperPath = blenderHelperPath,
                CancellationToken = cancellationToken
            });
            paths.AddRange(result.OutputPaths);
            triangles = result.Triangles;
            materials = result.MaterialCount;
            textures = result.TextureCount;
        }

        return new MeshExportResult
        {
            OutputPaths = paths,
            Triangles = triangles,
            MaterialCount = materials,
            TextureCount = textures
        };
    }

    private static string GbaClipName(string file, int clip)
    {
        var rom = new FileSystemAssetSource(file).TryReadCompanion(GbaLevelCarver.RomEntryName);
        return rom == null ? $"anim_{clip}" : GbaRiderClips.ExportName(rom, clip);
    }

    private static string SafeClipName(string name)
    {
        var safe = name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        return new string(safe).Trim('_');
    }

    public static Ps2SceneSubFormat DetectPs2SceneSubFormat(string file)
    {
        var subFormat = MeshTypeDetector.Detect(file).Ps2SubFormat;
        return subFormat == Ps2SceneSubFormat.None ? Ps2SceneSubFormat.Standard : subFormat;
    }

    /// <summary>
    ///     The output stem for a mesh file: the name with its longest known mesh
    ///     suffix removed. Superseded the three separate strip helpers that used
    ///     to disagree on compound suffixes.
    /// </summary>
    public static string StripKnownExtension(string file)
    {
        return MeshTypeDetector.GetStem(file);
    }

    public static bool ValidateFormat(string? value, out MeshOutputFormat format)
    {
        if (TryParseFormat(value, out format))
            return true;

        AnsiConsole.MarkupLine("[red]Error:[/] --format must be one of: glb, blend, both");
        return false;
    }
}
