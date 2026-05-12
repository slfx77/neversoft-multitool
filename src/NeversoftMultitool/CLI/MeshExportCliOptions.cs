using System.CommandLine;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
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
            Description = "Path to a Blender executable/helper for .blend export"
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
                    texturePath: texturePath,
                    skeletonPath: skeletonPath,
                    ddxPath: ddxPath,
                    psxPath: psxPath,
                    ddmTexturePath: ddmTexturePath,
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
        Ps2WorldzoneConverter.WorldzoneTimeOfDay worldzoneTimeOfDay = Ps2WorldzoneConverter.WorldzoneTimeOfDay.All,
        float worldzoneScale = 1f)
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
            WorldzoneScale = worldzoneScale
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
        var data = File.ReadAllBytes(file);
        if (Ps2GeomFile.IsPakMdl(data))
            return Ps2SceneSubFormat.PakMdl;
        if (ThawPs2SkinFile.IsThawPs2Skin(data))
            return Ps2SceneSubFormat.ThawSkin;
        if (ThawPs2SkinFile.IsPakSkin(data))
            return Ps2SceneSubFormat.PakSkin;
        return Ps2SceneSubFormat.Standard;
    }

    public static string StripKnownExtension(string file, IEnumerable<string> extensions)
    {
        var fileName = Path.GetFileName(file);
        var matchedExt = extensions.FirstOrDefault(ext =>
            fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        return matchedExt != null ? fileName[..^matchedExt.Length] : Path.GetFileNameWithoutExtension(fileName);
    }

    public static string StripColExtension(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        if (stem.EndsWith(".col", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        return stem;
    }

    public static bool ValidateFormat(string? value, out MeshOutputFormat format)
    {
        if (TryParseFormat(value, out format))
            return true;

        AnsiConsole.MarkupLine("[red]Error:[/] --format must be one of: glb, blend, both");
        return false;
    }
}
