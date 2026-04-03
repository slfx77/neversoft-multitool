using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Collision;
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

        var command = new Command("col", "Convert collision (.col) files to glTF (.glb)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, verbose));
        });

        return command;
    }

    private static int Execute(string input, string output, bool verbose)
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

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] COL file(s)");

        var stopwatch = Stopwatch.StartNew();
        var totalTriangles = 0;
        var converted = 0;
        var failed = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            // Strip compound extension: Arrow.col.xbx → Arrow
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.EndsWith(".col", StringComparison.OrdinalIgnoreCase))
                stem = stem[..^4];

            try
            {
                var data = File.ReadAllBytes(file);
                if (!ColFile.IsColFile(data))
                {
                    if (verbose)
                        AnsiConsole.MarkupLine($"  {fileName}: [yellow]Not a COL file[/]");
                    continue;
                }

                var scene = ColFile.Parse(data);
                var outputFile = Path.Combine(output, stem + ".glb");
                var triangles = ColGltfWriter.Write(scene, outputFile);

                totalTriangles += triangles;
                converted++;

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  {fileName}: [green]{triangles:N0}[/] tris, " +
                        $"{scene.Objects.Length} objects, {scene.TotalVertices:N0} verts");
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (verbose)
                    AnsiConsole.WriteException(ex);
                AnsiConsole.MarkupLine(
                    $"  {fileName}: [red]{ex.Message.EscapeMarkup()}[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"\nConverted [green]{converted}[/] files ({totalTriangles:N0} triangles) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s" +
            (failed > 0 ? $", [red]{failed} failed[/]" : ""));

        return failed > 0 ? 1 : 0;
    }
}
