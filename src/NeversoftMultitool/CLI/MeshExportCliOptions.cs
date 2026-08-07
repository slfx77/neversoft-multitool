using System.CommandLine;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats;
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
        float worldzoneScale = 1f)
    {
        Directory.CreateDirectory(output);

        var converted = 0;
        var failed = 0;
        var totalTriangles = 0;

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var fileName = Path.GetFileName(file);
            try
            {
                var result = ExportFile(
                    file,
                    output,
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
                    worldzoneScale: worldzoneScale);

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
            catch (Exception ex)
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
        string? psxLightPreset = null)
    {
        var stem = outputStem ?? Path.GetFileNameWithoutExtension(file);
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
            PsxLightPreset = psxLightPreset
        });

        return ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = output,
                OutputStem = document.Name,
                Format = format,
                BlenderHelperPath = blenderHelperPath,
                WorldzoneTimeOfDay = worldzoneTimeOfDay,
                WorldzoneScale = worldzoneScale,
                CancellationToken = cancellationToken
            });
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
