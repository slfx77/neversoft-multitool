using System.CommandLine;
using System.Globalization;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Texture.Nds;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Decodes the Vicarious Visions DS carts' textures to PNG.
///
///     A texture is split across two GOB files: a BANK holding the GX parameters
///     and palettes (<see cref="NdsTextureBank" />), and a separate texel blob the
///     loader names <c>.\%08x.texture.bin</c> from the bank record's id. This
///     command therefore works on the container — a <c>.gob</c> beside its
///     <c>.gfc</c>, or a <c>.nds</c> cart, which it opens and walks straight
///     through — so both halves are in hand.
/// </summary>
public static class NdsTextureCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "A DS cart (.nds) or its GOB container (.gob, beside its .gfc)"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for the decoded PNG textures"
        };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "List each texture" };

        var command = new Command("nds-texture", "Decode Nintendo DS (Vicarious Visions) textures to PNG");
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
            AnsiConsole.MarkupLine($"[red]Input not found:[/] {Markup.Escape(input)}");
            return 1;
        }

        using var container = NdsContainer.Open(input);
        if (container == null)
        {
            AnsiConsole.MarkupLine(
                "[red]No GOB container found.[/] Pass a DS cart (.nds) or a .gob beside its .gfc.");
            return 1;
        }

        var output = outputDir ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
            Path.GetFileNameWithoutExtension(input) + "_textures");
        Directory.CreateDirectory(output);

        // Index the container's texel blobs by the id their name encodes, so a bank
        // record resolves without re-hashing anything.
        var byPixelId = new Dictionary<uint, ArchiveEntry>();
        foreach (var entry in container.Entries)
        {
            var name = entry.Name;
            if (!name.EndsWith(".texture.bin", StringComparison.OrdinalIgnoreCase))
                continue;
            if (uint.TryParse(name.AsSpan(0, Math.Min(8, name.Length)),
                    NumberStyles.HexNumber, null, out var id))
            {
                byPixelId[id] = entry;
            }
        }

        long? PixelLength(uint id) => byPixelId.TryGetValue(id, out var e) ? e.Size : null;

        var banks = 0;
        var written = 0;
        var missing = 0;
        var skipped = 0;

        foreach (var entry in container.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data;
            try
            {
                data = container.ReadEntry(entry);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                continue;
            }

            if (!NdsTextureBank.TryParseValidated(data, PixelLength, out var textures))
                continue;

            banks++;
            var stem = Path.GetFileNameWithoutExtension(entry.Name);
            for (var i = 0; i < textures.Count; i++)
            {
                var texture = textures[i];
                if (!byPixelId.TryGetValue(texture.PixelId, out var pixelEntry))
                {
                    missing++;
                    continue;
                }

                try
                {
                    var rgba = NdsTextureDecoder.Decode(texture, container.ReadEntry(pixelEntry));
                    var path = Path.Combine(output, $"{stem}_{i:D3}_{texture.PixelId:x8}.png");
                    ImageWriter.WritePng(path, texture.Width, texture.Height, rgba);
                    written++;
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine(
                            $"  {Markup.Escape(Path.GetFileName(path))} " +
                            $"[grey]{texture.Width}x{texture.Height} {texture.Format}[/]");
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                    skipped++;
                    if (verbose)
                        AnsiConsole.MarkupLine($"  [yellow]skip[/] {texture.PixelId:x8}: {Markup.Escape(ex.Message)}");
                }
            }
        }

        AnsiConsole.MarkupLine(
            $"Decoded [green]{written}[/] textures from [green]{banks}[/] banks" +
            (missing > 0 ? $", [yellow]{missing}[/] missing texel blobs" : "") +
            (skipped > 0 ? $", [yellow]{skipped}[/] unsupported" : ""));
        return written > 0 ? 0 : 1;
    }
}
