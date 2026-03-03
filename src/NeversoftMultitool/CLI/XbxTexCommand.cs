using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.XbxScene;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class XbxTexCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to an Xbox/PC TEX or IMG file (.tex.xbx, .img.xbx) or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for extracted PNG files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("xbxtex", "Extract textures from Xbox/PC TEX/IMG files (.xbx) to PNG");
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
            files = Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(IsXbxTextureFile)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Xbox TEX/IMG files found.[/]");
            return 0;
        }

        // Probe for unsupported files
        var (supported, unsupported) = FormatProbe.PartitionFiles(files, FormatProbe.ProbeTexture);
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
            AnsiConsole.MarkupLine("[yellow]No supported Xbox TEX/IMG files to process.[/]");
            return 0;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Processing [green]{files.Count}[/] Xbox TEX/IMG file(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var totalTextures = 0;

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            // Strip compound extensions: .tex.xbx → base name, .img.xbx → base name
            var stem = Path.GetFileNameWithoutExtension(filename);
            if (stem.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                stem = stem[..^4];

            var lower = filename.ToLowerInvariant();
            var isImg = lower.EndsWith(".img.xbx") || lower.EndsWith(".img.wpc");

            if (isImg)
            {
                var result = XbxImgFile.Parse(file);
                if (!result.Success)
                {
                    failed++;
                    if (verbose)
                        AnsiConsole.MarkupLine($"  {filename}: [red]{result.ErrorMessage}[/]");
                    continue;
                }

                var outPath = Path.Combine(output, stem + ".png");
                var count = XbxImgFile.SaveAsPng(result, outPath);
                totalTextures += count;
                converted++;

                if (verbose)
                    AnsiConsole.MarkupLine($"  {filename}: [green]1 texture, {count} PNG[/]");
            }
            else
            {
                var result = XbxTexFile.Parse(file);
                if (!result.Success)
                {
                    failed++;
                    if (verbose)
                        AnsiConsole.MarkupLine($"  {filename}: [red]{result.ErrorMessage}[/]");
                    continue;
                }

                var count = XbxTexFile.SaveAllAsPng(result, output, stem);
                totalTextures += count;
                converted++;

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  {filename}: [green]{result.Textures.Count} textures, {count} PNGs[/]");
                }
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} files " +
            $"({totalTextures:N0} textures, {failed} failed) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    private static bool IsXbxTextureFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tex.xbx", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".img.xbx", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".tex.wpc", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".img.wpc", StringComparison.OrdinalIgnoreCase);
    }
}
