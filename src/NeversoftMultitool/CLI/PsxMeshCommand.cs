using System.CommandLine;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class PsxMeshCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PSX file or directory containing PSX files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for glTF (.glb) files",
            DefaultValueFactory = _ => "TestOutput/PsxMesh"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("psx-mesh", "Convert PSX model files to glTF (.glb) or Blender (.blend)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(formatOption);
        command.Options.Add(blenderHelperOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            if (!MeshExportCliOptions.ValidateFormat(parseResult.GetValue(formatOption), out var format))
                return Task.FromResult(1);
            var blenderHelper = parseResult.GetValue(blenderHelperOption);

            return Task.FromResult(Execute(input, output, verbose, format, blenderHelper, cancellationToken));
        });

        return command;
    }

    private static int Execute(
        string input,
        string output,
        bool verbose,
        MeshOutputFormat format,
        string? blenderHelperPath,
        CancellationToken cancellationToken)
    {
        List<string> psxFiles;

        if (File.Exists(input))
        {
            psxFiles = [input];
        }
        else if (Directory.Exists(input))
        {
            psxFiles = Directory.GetFiles(input, "*.psx",
                    new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (psxFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .psx files found.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{psxFiles.Count}[/] PSX file(s)");
        return MeshExportCliOptions.ExportFiles(
            psxFiles,
            output,
            ModelSourceKind.Psx,
            format,
            blenderHelperPath,
            verbose,
            cancellationToken);
    }
}
