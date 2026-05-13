using System.CommandLine;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class XbxSceneCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to an Xbox/PC scene file (.skin.xbx, .mdl.xbx) or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for exported mesh files",
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
        var coordinateScaleOption = new Option<float>("--coordinate-scale", "--scale")
        {
            Description =
                "Multiply exported coordinates by this positive scale. Use 0.01 for scaled worldzone Blender inspection.",
            DefaultValueFactory = _ => 1f
        };
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("xbxscene",
            "Convert Xbox/PC scene files (SKIN/MDL) to glTF (.glb) or Blender (.blend) - THUG2 + THAW");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texPathOption);
        command.Options.Add(verboseOption);
        command.Options.Add(coordinateScaleOption);
        command.Options.Add(formatOption);
        command.Options.Add(blenderHelperOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var texPath = parseResult.GetValue(texPathOption);
            var verbose = parseResult.GetValue(verboseOption);
            var coordinateScale = parseResult.GetValue(coordinateScaleOption);
            if (!MeshExportCliOptions.ValidateFormat(parseResult.GetValue(formatOption), out var format))
                return Task.FromResult(1);
            var blenderHelper = parseResult.GetValue(blenderHelperOption);
            if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --coordinate-scale must be a finite positive number");
                return Task.FromResult(1);
            }

            return Task.FromResult(Execute(input, output, texPath, verbose, format, blenderHelper, coordinateScale,
                cancellationToken));
        });

        return command;
    }

    private static int Execute(string input, string output,
        string? texPath, bool verbose, MeshOutputFormat format, string? blenderHelperPath,
        float coordinateScale,
        CancellationToken cancellationToken)
    {
        var files = CollectFiles(input);
        if (files == null) return 1;
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Xbox scene files found.[/]");
            return 0;
        }

        var allExts = XbxSceneFile.SupportedExtensions
            .Concat(ThawSceneFile.SupportedExtensions)
            .Distinct()
            .ToArray();
        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] Xbox scene file(s)");
        return MeshExportCliOptions.ExportFiles(
            files,
            output,
            ModelSourceKind.XbxScene,
            format,
            blenderHelperPath,
            verbose,
            cancellationToken,
            file => MeshExportCliOptions.StripKnownExtension(file, allExts),
            texturePath: texPath,
            worldzoneScale: coordinateScale);
    }

    private static List<string>? CollectFiles(string input)
    {
        if (File.Exists(input))
            return [input];

        if (Directory.Exists(input))
        {
            var allExts = XbxSceneFile.SupportedExtensions
                .Concat(ThawSceneFile.SupportedExtensions)
                .Distinct()
                .ToArray();
            return Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(p => allExts.Any(ext => Path.GetFileName(p).EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
        return null;
    }
}
