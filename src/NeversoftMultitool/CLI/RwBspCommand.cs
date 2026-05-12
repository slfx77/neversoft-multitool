using System.CommandLine;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class RwBspCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a RenderWare BSP file (.bsp) or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for .glb files",
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
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("rwbsp", "Convert RenderWare BSP level files to glTF (.glb) or Blender (.blend)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texPathOption);
        command.Options.Add(verboseOption);
        command.Options.Add(formatOption);
        command.Options.Add(blenderHelperOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var texPath = parseResult.GetValue(texPathOption);
            var verbose = parseResult.GetValue(verboseOption);
            if (!MeshExportCliOptions.ValidateFormat(parseResult.GetValue(formatOption), out var format))
                return Task.FromResult(1);
            var blenderHelper = parseResult.GetValue(blenderHelperOption);

            return Task.FromResult(Execute(input, output, texPath, verbose, format, blenderHelper, cancellationToken));
        });

        return command;
    }

    private static int Execute(string input, string output,
        string? texPath, bool verbose, MeshOutputFormat format, string? blenderHelperPath,
        CancellationToken cancellationToken)
    {
        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*.bsp", SearchOption.AllDirectories)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No BSP files found.[/]");
            return 0;
        }

        // Probe for unsupported files
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
            AnsiConsole.MarkupLine("[yellow]No supported BSP files to process.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] BSP file(s)");
        return MeshExportCliOptions.ExportFiles(
            files,
            output,
            ModelSourceKind.RenderWareBsp,
            format,
            blenderHelperPath,
            verbose,
            cancellationToken,
            texturePath: texPath);
    }
}
