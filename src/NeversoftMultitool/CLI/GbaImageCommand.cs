using System.CommandLine;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Texture.Gba;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Extracts the full-screen menu / title / logo images that the Vicarious
///     Visions GBA Tony Hawk engine stores as BIOS-LZ77 streams, to PNG. The images
///     are located by content (a stream decoding to 240×160 paletted bytes), so no
///     filename table is needed; see <see cref="GbaRomImages" />.
/// </summary>
public static class GbaImageCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a GBA ROM (Vicarious Visions Tony Hawk line)"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for the extracted PNG images"
        };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "List each extracted image" };

        var command = new Command("gba-image", "Extract full-screen BIOS-LZ77 images from a GBA ROM to PNG");
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

        var images = GbaRomImages.ScanFullScreenImages(rom);
        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No full-screen BIOS-LZ77 images found[/] in this ROM");
            return 0;
        }

        var dir = outputDir ?? Path.Combine("TestOutput", Path.GetFileNameWithoutExtension(input) + "-gba-images");
        Directory.CreateDirectory(dir);
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(dir, image.Name + ".png");
            ImageWriter.WritePng(path, GbaRomImages.ScreenWidth, GbaRomImages.ScreenHeight, image.Rgba);
            if (verbose)
                AnsiConsole.MarkupLine(
                    $"  {image.Name}.png  0x{image.RomOffset:X8}  {image.Layout}  "
                    + $"pal 0x{image.PaletteOffset:X8} ({image.PaletteColors} colours)");
        }

        AnsiConsole.MarkupLine($"Extracted [green]{images.Count}[/] PNG images to [green]{Markup.Escape(dir)}[/]");
        return 0;
    }
}
