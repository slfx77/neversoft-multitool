using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.NextGen;
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class XbxTexCommand
{
    private static readonly string[] ImageSuffixes = [".img.xbx", ".img.wpc", ".img.ngc", ".img"];

    private static readonly string[] SupportedSuffixes =
    [
        ".tex.xbx", ".img.xbx", ".tex.wpc", ".img.wpc", ".tex.ngc", ".img.ngc", ".stex", ".tex", ".img",
        // Next-gen FACECAA7 dictionaries (THAW/P8/PG on Xbox 360 and PS3).
        ".tex.xen", ".stex.xen", ".tex.ps3", ".stex.ps3"
    ];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description =
                "Path to an Xbox/PC/next-gen TEX/IMG file (.tex.xbx, .img.xbx, .tex.wpc, .img.wpc, .stex, .tex.xen, .tex.ps3, extracted .tex) or directory"
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

        var command = new Command("xbxtex", "Extract textures from Xbox/PC TEX/IMG files to PNG");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, verbose, cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool verbose,
        CancellationToken cancellationToken = default)
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
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {Markup.Escape(input)}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Xbox TEX/IMG files found.[/]");
            return 0;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Processing [green]{files.Count}[/] Xbox TEX/IMG file(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var totalTextures = 0;

        // Directory.GetFiles preserves the caller's relative/absolute path form.
        // Keep the root in that same form so mirrored collision paths are truly
        // relative even when the command was invoked with a relative directory.
        var inputRoot = Directory.Exists(input) ? input : null;
        var outputPlan = MeshOutputPathPlanner.Plan(files, GetOutputStem, inputRoot);

        foreach (var planned in outputPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = planned.File;
            var filename = Path.GetFileName(file);
            var plannedOutput = string.IsNullOrEmpty(planned.Subdirectory)
                ? output
                : Path.Combine(output, planned.Subdirectory);

            var isImg = OrdinalFileName.HasAnySuffix(filename, ImageSuffixes);

            if (isImg)
            {
                var result = XbxImgFile.Parse(file);
                if (!result.Success)
                    result = ThawImgFile.Parse(file);
                if (!result.Success)
                    result = NgcTexFile.Parse(file); // THAW GameCube .img.ngc
                if (!result.Success)
                {
                    failed++;
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine(
                            $"  {Markup.Escape(filename)}: " +
                            $"[red]{Markup.Escape(result.ErrorMessage ?? "Unknown error")}[/]");
                    }
                    continue;
                }

                var outPath = Path.Combine(plannedOutput, planned.Stem + ".png");
                var count = XbxImgFile.SaveAsPng(result, outPath);
                if (result.Textures.Count > 0 && count == 0)
                {
                    failed++;
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine(
                            $"  {Markup.Escape(filename)}: [red]No decodable textures[/]");
                    }

                    continue;
                }

                totalTextures += count;
                converted++;

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(filename)}: [green]1 texture, {count} PNG[/]");
                }
            }
            else
            {
                var result = XbxTexFile.Parse(file);
                if (!result.Success)
                    result = ThawTexFile.Parse(file); // Try THAW 0xABADD00D format
                if (!result.Success)
                    result = NgcTexFile.Parse(file); // THAW GameCube .tex.ngc
                if (!result.Success)
                    result = ParseNextGenTex(file); // FACECAA7 (Xbox 360 / PS3)
                if (!result.Success)
                {
                    failed++;
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine(
                            $"  {Markup.Escape(filename)}: " +
                            $"[red]{Markup.Escape(result.ErrorMessage ?? "Unknown error")}[/]");
                    }
                    continue;
                }

                var count = XbxTexFile.SaveAllAsPng(result, plannedOutput, planned.Stem);
                if (result.Textures.Count > 0 && count == 0)
                {
                    failed++;
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine(
                            $"  {Markup.Escape(filename)}: [red]No decodable textures[/]");
                    }

                    continue;
                }

                totalTextures += count;
                converted++;

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(filename)}: " +
                        $"[green]{result.Textures.Count} textures, {count} PNGs[/]");
                }
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} files " +
            $"({totalTextures:N0} textures, {failed} failed) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return failed == 0 ? 0 : 1;
    }

    private static string GetOutputStem(string file)
    {
        // Preserve the established compound-extension rule for every supported suffix.
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(file));
        if (stem.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        return stem;
    }

    private static bool IsXbxTextureFile(string path)
    {
        var name = Path.GetFileName(path);
        return OrdinalFileName.HasAnySuffix(name, SupportedSuffixes);
    }

    /// <summary>
    ///     Parses a next-gen FACECAA7 dictionary, supplying the PS3 VRAM twin
    ///     when there is one — a PS3 dictionary holds no pixels of its own.
    /// </summary>
    private static Ps2TexResult ParseNextGenTex(string file)
    {
        var data = File.ReadAllBytes(file);
        if (!NextGenTexFile.IsNextGenTex(data))
            return Ps2TexResult.Fail("Not a FACECAA7 texture dictionary");

        return NextGenTexFile.Parse(data, NextGenVramTwinLocator.TryLoad(file, data));
    }
}
