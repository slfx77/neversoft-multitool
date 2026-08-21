using System.CommandLine;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Gba;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Reconstructs the isometric level images from a Vicarious Visions GBA Tony
///     Hawk ROM to PNG (the "render a level" deliverable, in 2D — the engine has no
///     meshes). See <see cref="GbaLevelImages" />.
/// </summary>
public static class GbaLevelCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a Vicarious Visions GBA Tony Hawk ROM"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for the reconstructed level PNGs"
        };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "List each level" };

        var command = new Command("gba-level", "Reconstruct isometric level images from a GBA ROM to PNG");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(verboseOption),
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input, string? outputDir, bool verbose, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(input);
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var levels = GbaLevelImages.FindLevels(rom);
        if (levels.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No isometric level table found[/] in this ROM");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{levels.Count}[/] levels");
        var dir = outputDir ?? Path.Combine("TestOutput", Path.GetFileNameWithoutExtension(input) + "-gba-levels");
        Directory.CreateDirectory(dir);

        var written = 0;
        for (var i = 0; i < levels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = GbaLevelImages.RenderLevel(rom, levels[i]);
            if (bitmap is null)
                continue;
            var path = Path.Combine(dir, $"level_{i:D2}.png");
            ImageWriter.WritePng(path, bitmap.Value.Width, bitmap.Value.Height, GbaLevelImages.ToRgba(bitmap.Value));
            written++;
            if (verbose)
                AnsiConsole.MarkupLine(
                    $"  level_{i:D2}.png  obj 0x{levels[i].ObjectListAddress:X8}  "
                    + $"elem 0x{levels[i].ElementLibraryAddress:X8} ({levels[i].ElementCount} tiles)  "
                    + $"{bitmap.Value.Width}x{bitmap.Value.Height}");
        }

        AnsiConsole.MarkupLine($"Reconstructed [green]{written}[/] level images to [green]{Markup.Escape(dir)}[/]");
        AnsiConsole.MarkupLine("[grey]Note: 2-tone ink coverage; per-level colour is a separate pass, not yet decoded.[/]");
        return 0;
    }
}
