using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class Ps2TexCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PS2 TEX/IMG file or directory containing them"
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

        var command = new Command("ps2tex", "Extract textures from PS2 TEX/IMG files to PNG");
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
                .Where(IsPs2TextureFile)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No TEX/IMG files found.[/]");
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
            AnsiConsole.MarkupLine("[yellow]No supported TEX/IMG files to process.[/]");
            return 0;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Processing [green]{files.Count}[/] TEX/IMG file(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var totalTextures = 0;

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            // Strip compound extensions like .tex.ps2 → base name
            var stem = Path.GetFileNameWithoutExtension(filename);
            if (stem.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                stem = stem[..^4];

            var result = Ps2TexFile.Parse(file);
            if (!result.Success)
                result = ThawSceneTexFile.Parse(file);

            if (!result.Success)
            {
                failed++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  {filename}: [red]{result.ErrorMessage}[/]");
                continue;
            }

            var count = Ps2TexFile.SaveAllAsPng(result, output, stem);
            totalTextures += count;
            converted++;

            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  {filename}: [green]{result.Textures.Count} textures, {count} PNGs[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} files " +
            $"({totalTextures:N0} textures, {failed} failed) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    /// <summary>
    ///     Detects PS2 texture files by extension (.tex, .img) or compound extension (.tex.ps2, .img.ps2).
    /// </summary>
    private static bool IsPs2TextureFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".tex.ps2", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".img.ps2", StringComparison.OrdinalIgnoreCase);
    }
}
