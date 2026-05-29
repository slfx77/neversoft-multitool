using System.CommandLine;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using PsxAnalyzer.Utils;
using Spectre.Console;

namespace PsxAnalyzer.Commands;

public static class DumpCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a .psx file or directory containing .psx files"
        };
        var bytesOption = new Option<int>("-n", "--bytes")
        {
            Description = "Number of bytes to dump after the texture count field",
            DefaultValueFactory = _ => 512
        };

        var command = new Command("dump", "Parse PSX file structure and dump texture section data");
        command.Arguments.Add(inputArgument);
        command.Options.Add(bytesOption);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var dumpBytes = parseResult.GetValue(bytesOption);

            var files = File.Exists(input)
                ? [input]
                : Directory.Exists(input)
                    ? Directory.GetFiles(input, "*.psx")
                    : [];

            if (files.Length == 0)
            {
                AnsiConsole.MarkupLine("[red]No .psx files found.[/]");
                return Task.FromResult(1);
            }

            foreach (var file in files)
            {
                DumpFile(file, dumpBytes);
                AnsiConsole.WriteLine();
            }

            return Task.FromResult(0);
        });

        return command;
    }

    private static void DumpFile(string filePath, int dumpBytes)
    {
        var filename = Path.GetFileName(filePath);
        var rule = new Rule($"[bold]{filename}[/]");
        rule.LeftJustified();
        AnsiConsole.Write(rule);

        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            AnsiConsole.MarkupLine($"  File size: [cyan]{stream.Length:N0}[/] bytes");

            // Validate magic
            var magic = reader.ReadBytes(4);
            AnsiConsole.MarkupLine($"  Magic: [cyan]{magic[0]:X2} {magic[1]:X2} {magic[2]:X2} {magic[3]:X2}[/]");
            if (!PsxLibrary.IsValidMagic(magic))
            {
                AnsiConsole.MarkupLine("  [red]Invalid magic number[/]");
                return;
            }

            // Parse structure
            PsxLibrary.SkipModelData(reader);
            var posAfterModel = reader.BaseStream.Position;
            AnsiConsole.MarkupLine($"  Model data ends at: [cyan]0x{posAfterModel:X}[/]");

            var texNames = PsxLibrary.ReadTextureInfo(reader);
            AnsiConsole.MarkupLine($"  Texture name hashes: [cyan]{texNames.Length}[/]");
            foreach (var name in texNames)
            {
                AnsiConsole.MarkupLine($"    0x{name:X8}");
            }

            var palette4Bit = PsxLibrary.ReadPalettes(reader, 16);
            AnsiConsole.MarkupLine($"  4-bit palettes: [cyan]{palette4Bit.Count}[/]");

            var palette8Bit = PsxLibrary.ReadPalettes(reader, 256);
            AnsiConsole.MarkupLine($"  8-bit palettes: [cyan]{palette8Bit.Count}[/]");

            var texCountOffset = reader.BaseStream.Position;
            var numActualTex = reader.ReadUInt32();
            AnsiConsole.MarkupLine($"  Texture count at [cyan]0x{texCountOffset:X}[/]: [bold]{(numActualTex == 0xFFFFFFFF ? "0xFFFFFFFF" : numActualTex.ToString())}[/]");

            if (numActualTex == 0xFFFFFFFF)
            {
                AnsiConsole.MarkupLine("  [yellow]PVR-T marker detected![/]");
                var remaining = stream.Length - reader.BaseStream.Position;
                AnsiConsole.MarkupLine($"  Remaining bytes: [cyan]{remaining:N0}[/]");

                var toDump = (int)Math.Min(dumpBytes, remaining);
                var data = reader.ReadBytes(toDump);
                BinaryHelpers.HexDump(data, 0, toDump);
            }
            else
            {
                AnsiConsole.MarkupLine($"  Reading [cyan]{numActualTex}[/] texture headers...");

                // Skip unknown data per texture
                for (var i = 0; i < numActualTex; i++)
                    reader.ReadBytes(4);

                for (var i = 0; i < numActualTex; i++)
                {
                    var header = PsxLibrary.GetTextureHeader(reader);
                    var palType = header.PalSize switch
                    {
                        16 => "4-bit",
                        256 => "8-bit",
                        65536 => $"16-bit (fmt=0x{header.PixelFormat:X})",
                        _ => $"unknown ({header.PalSize})"
                    };
                    AnsiConsole.MarkupLine(
                        $"    [dim][[{i}]][/] {header.Width}x{header.Height} {palType} " +
                        $"texId=0x{header.TexId:X8} size={header.Size} offset=0x{header.Offset:X}");

                    // Skip texture data
                    if (header.PalSize == 65536)
                    {
                        reader.BaseStream.Seek(header.TextureOffset + header.Size, SeekOrigin.Begin);
                    }
                    else if (header.PalSize == 16)
                    {
                        var padWidth = (header.Width + 0x3) & ~0x3;
                        padWidth >>= 1;
                        var realLen = padWidth * header.Height;
                        if (header.Height % 2 != 0 && padWidth % 4 != 0) realLen += 2;
                        reader.BaseStream.Seek(header.TextureOffset + realLen, SeekOrigin.Begin);
                    }
                    else if (header.PalSize == 256)
                    {
                        var padWidth = (header.Width + 0x1) & ~0x1;
                        var realLen = padWidth * header.Height;
                        if (header.Height % 2 != 0 && padWidth % 4 != 0) realLen += 2;
                        reader.BaseStream.Seek(header.TextureOffset + realLen, SeekOrigin.Begin);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]Error: {ex.Message}[/]");
        }
    }

}
