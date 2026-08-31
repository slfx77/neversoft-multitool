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
            return ExecuteLaterCart(rom, input, outputDir, verbose, cancellationToken);

        AnsiConsole.MarkupLine($"Found [green]{levels.Count}[/] levels");
        var dir = outputDir ?? Path.Combine("TestOutput", Path.GetFileNameWithoutExtension(input) + "-gba-levels");
        Directory.CreateDirectory(dir);

        var written = 0;
        for (var i = 0; i < levels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteLevelImages(rom, levels[i], i, dir, verbose))
                written++;
        }

        AnsiConsole.MarkupLine($"Reconstructed [green]{written}[/] level images to [green]{Markup.Escape(dir)}[/]");
        AnsiConsole.MarkupLine(
            "[grey]Each level: the full-colour isometric surface (level_NN_colour, the actual "
            + "game appearance), the tile-detail render (level_NN), the geometry heightfield "
            + "(level_NN_iso), and the palette (level_NN_palette).[/]");
        return 0;
    }

    /// <summary>
    ///     THPS4, THUG, THUG2 and American Sk8land share a different level-art record
    ///     from THPS2's, with no collision grid and no colour surface, so they get the
    ///     one view they have: the composited isometric art as ink coverage.
    /// </summary>
    private static int ExecuteLaterCart(
        byte[] rom, string input, string? outputDir, bool verbose, CancellationToken cancellationToken)
    {
        var levels = GbaLaterLevelArt.FindLevels(rom);
        if (levels.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No isometric level table found[/] in this ROM");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{levels.Count}[/] levels");
        var dir = outputDir ?? Path.Combine("TestOutput", Path.GetFileNameWithoutExtension(input) + "-gba-levels");
        Directory.CreateDirectory(dir);

        var written = 0;
        foreach (var level in levels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var render = GbaLaterLevelArt.Render(rom, level);
            if (render is null)
                continue;
            ImageWriter.WritePng(
                Path.Combine(dir, $"level_{level.Index:D2}.png"),
                render.Value.Width, render.Value.Height, render.Value.Rgba);
            written++;
            if (verbose)
                AnsiConsole.MarkupLine(
                    $"  level_{level.Index:D2}  art 0x{0x08000000 + level.ArtRecordOffset:X8}  "
                    + $"{level.PixelWidth}x{level.PixelHeight}  map {level.MapWidth}x{level.MapHeight}");
        }

        AnsiConsole.MarkupLine($"Reconstructed [green]{written}[/] level images to [green]{Markup.Escape(dir)}[/]");
        AnsiConsole.MarkupLine(
            "[grey]This cartridge's art is one bit deep, so each level is rendered as ink "
            + "coverage; its colour surface is a separate pass that is not yet identified.[/]");
        return 0;
    }

    // Writes every view for one level (tile detail + iso heightfield + palette).
    // Returns true when the main tile-detail render was produced.
    private static bool WriteLevelImages(
        byte[] rom, GbaLevelImages.GbaLevel level, int index, string dir, bool verbose)
    {
        var bitmap = GbaLevelImages.RenderLevel(rom, level);
        if (bitmap is null)
            return false;
        ImageWriter.WritePng(
            Path.Combine(dir, $"level_{index:D2}.png"),
            bitmap.Value.Width, bitmap.Value.Height, GbaLevelImages.ToRgba(bitmap.Value));

        // The true full-colour isometric surface — the actual game appearance,
        // composited from the ROM's pre-baked iso tile art + palette.
        var colour = GbaLevelImages.RenderColourSurface(rom, level);
        if (colour != null)
            ImageWriter.WritePng(
                Path.Combine(dir, $"level_{index:D2}_colour.png"),
                colour.Value.Width, colour.Value.Height, colour.Value.Rgba);

        // The 3D collision surface with each cell's REAL shape — ramps slope and
        // quarter-pipes curve, because the material height functions are executed
        // out of the ROM rather than treated as flat tops.
        var trueRecord = (int)(level.RecordAddress - 0x08000000) - 0x144;
        var iso = GbaCollisionRenderer.Render(rom, trueRecord);
        if (iso != null)
            ImageWriter.WritePng(
                Path.Combine(dir, $"level_{index:D2}_iso.png"),
                iso.Value.Width, iso.Value.Height, iso.Value.Rgba);

        // The collision grid drawn over the level's own art (which art is which
        // collision type), via the engine's stored per-level art origin.
        var overlay = colour != null
            ? GbaCollisionRenderer.RenderArtOverlay(
                rom, trueRecord, colour.Value.Width, colour.Value.Height, colour.Value.Rgba)
            : null;
        if (overlay != null)
            ImageWriter.WritePng(
                Path.Combine(dir, $"level_{index:D2}_overlay.png"),
                overlay.Value.Width, overlay.Value.Height, overlay.Value.Rgba);

        // Emit the level's real background palette (its true colour source).
        var palette = GbaLevelImages.TryGetPalette(rom, level);
        if (palette != null)
            ImageWriter.WritePng(Path.Combine(dir, $"level_{index:D2}_palette.png"), 256, 256, PaletteSwatch(palette));

        if (verbose)
            AnsiConsole.MarkupLine(
                $"  level_{index:D2}  obj 0x{level.ObjectListAddress:X8}  "
                + $"elem 0x{level.ElementLibraryAddress:X8} ({level.ElementCount} tiles)  "
                + $"{bitmap.Value.Width}x{bitmap.Value.Height}"
                + $"{(colour != null ? "  +colour" : "")}{(iso != null ? "  +iso" : "")}"
                + $"{(overlay != null ? "  +overlay" : "")}{(palette != null ? "  +palette" : "")}");
        return true;
    }

    // A 256×256 swatch of a 256-colour RGBA palette (16×16 grid of 16px cells).
    private static byte[] PaletteSwatch(byte[] palette)
    {
        var rgba = new byte[256 * 256 * 4];
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var index = (y / 16) * 16 + (x / 16);
                var o = (y * 256 + x) * 4;
                Array.Copy(palette, index * 4, rgba, o, 4);
            }
        }

        return rgba;
    }
}
