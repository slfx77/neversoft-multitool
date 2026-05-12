using System.CommandLine;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class ColCommand
{
    private static readonly string[] ColSuffixes = [".col.xbx", ".col.wpc", ".col.ps2"];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a COL collision file (.col.xbx, .col) or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for .glb files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("col", "Convert collision (.col) files to glTF (.glb) or Blender (.blend)");
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
        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                .Where(static file =>
                {
                    var fileName = Path.GetFileName(file);
                    return OrdinalFileName.HasAnySuffix(fileName, ColSuffixes)
                           || OrdinalFileName.HasExtension(file, ".col");
                })
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No COL files found.[/]");
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
            AnsiConsole.MarkupLine("[yellow]No supported COL files to process.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] COL file(s)");
        return MeshExportCliOptions.ExportFiles(
            files,
            output,
            ModelSourceKind.Collision,
            format,
            blenderHelperPath,
            verbose,
            cancellationToken,
            MeshExportCliOptions.StripColExtension);
    }
}
