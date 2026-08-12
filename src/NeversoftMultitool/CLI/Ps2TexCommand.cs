using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
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
        var gifQwordOrderOption = new Option<string>("--gif-qword-order")
        {
            Description =
                "Diagnostic: reorder 32-bit words inside each 16-byte GIF IMAGE qword before CT32/CT16 writes. Use a 4-digit permutation such as 0123 or 2031.",
            DefaultValueFactory = _ => "0123"
        };

        var command = new Command("ps2tex", "Extract textures from PS2 TEX/IMG files to PNG");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(gifQwordOrderOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var gifQwordOrderText = parseResult.GetValue(gifQwordOrderOption)!;

            return Task.FromResult(Execute(
                input,
                output,
                verbose,
                gifQwordOrderText,
                cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool verbose,
        string gifQwordOrderText,
        CancellationToken cancellationToken = default)
    {
        if (!Ps2GifQwordWordOrder.TryParse(gifQwordOrderText, out var gifQwordWordOrder))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Invalid GIF qword order '{Markup.Escape(gifQwordOrderText)}'. Expected a 4-digit permutation of 0-3, for example 0123 or 2031.");
            return 1;
        }

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
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {Markup.Escape(input)}");
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

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);

        // Separate zone TEX files from standard TEX/IMG files.
        // Zone TEX files must be processed together (merged VRAM map) because
        // textures can reference pixel data from one file and CLUT data from another.
        var standardFiles = new List<string>();
        var zoneTexFiles = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var data = File.ReadAllBytes(file);
                if (ThawZoneTexFile.IsThawZoneTex(data))
                    zoneTexFiles.Add(file);
                else
                    standardFiles.Add(file);
            }
            catch
            {
                standardFiles.Add(file);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var totalTextures = 0;

        // Process zone TEX files as a batch with merged VRAM map
        if (zoneTexFiles.Count > 0)
        {
            var zoneResult = ProcessZoneTexFiles(
                zoneTexFiles,
                output,
                verbose,
                gifQwordWordOrder,
                cancellationToken);
            totalTextures += zoneResult.Textures;
            if (zoneResult.Textures > 0) converted += zoneResult.FilesConverted;
            failed += zoneResult.FilesFailed;
        }

        // Process standard TEX/IMG files individually. Only colliding stems mirror
        // their source folders; unique and direct-file inputs retain the flat layout.
        var standardPlan = MeshOutputPathPlanner.Plan(
            standardFiles,
            GetStandardStem,
            Directory.Exists(input) ? input : null);
        if (standardPlan.Count > 0)
            AnsiConsole.MarkupLine($"Processing [green]{standardPlan.Count}[/] standard TEX/IMG file(s)");

        foreach (var (file, subdirectory, stem) in standardPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = Path.GetFileName(file);

            var result = Ps2TexFile.Parse(file);
            if (!result.Success)
                result = ThawSceneTexFile.Parse(file);

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

            var fileOutput = subdirectory.Length == 0
                ? output
                : Path.Combine(output, subdirectory);
            var count = Ps2TexFile.SaveAllAsPng(result, fileOutput, stem);
            totalTextures += count;
            converted++;

            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  {Markup.Escape(filename)}: " +
                    $"[green]{result.Textures.Count} textures, {count} PNGs[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} files " +
            $"({totalTextures:N0} textures, {failed} failed) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    ///     Process all zone TEX files: decode textures from record metadata using
    ///     upload-snapshot VRAM replay.
    /// </summary>
    private static ZoneTexBatchResult ProcessZoneTexFiles(
        List<string> zoneTexFiles,
        string output,
        bool verbose,
        Ps2GifQwordWordOrder gifQwordWordOrder,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(
            $"Processing [green]{zoneTexFiles.Count}[/] THAW zone TEX file(s)");

        var textureMap = new Dictionary<uint, Ps2Texture>();

        // Decode all textures from each zone TEX file using prepared source slots, with
        // upload-snapshot fallback when a slot-backed entry cannot be resolved.
        foreach (var file in zoneTexFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = File.ReadAllBytes(file);
            var textures = ThawZoneTexFile.DecodeAllFromFile(data, gifQwordWordOrder);

            foreach (var texture in textures)
                textureMap.TryAdd(texture.Checksum, texture);

            if (verbose)
            {
                var entries = ThawZoneTexFile.ParseHeaderEntries(data);
                AnsiConsole.MarkupLine(
                    $"  {Markup.Escape(Path.GetFileName(file))}: " +
                    $"[green]{entries.Count} records[/], " +
                    $"[green]{textures.Count} textures decoded[/]");
            }
        }

        if (textureMap.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No textures decoded from zone TEX files[/]");
            return new ZoneTexBatchResult(0, 0, zoneTexFiles.Count);
        }

        // Save as PNGs under "zone_tex" subdirectory
        cancellationToken.ThrowIfCancellationRequested();
        var result = new Ps2TexResult(textureMap.Values.ToList());
        var count = Ps2TexFile.SaveAllAsPng(result, output, "zone_tex");

        AnsiConsole.MarkupLine(
            $"Zone TEX: [green]{count} textures[/] decoded from prepared sources");

        return new ZoneTexBatchResult(count, zoneTexFiles.Count, 0);
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

    private static string GetStandardStem(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        if (stem.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        return stem;
    }

    private record struct ZoneTexBatchResult(int Textures, int FilesConverted, int FilesFailed);
}
