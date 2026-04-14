using System.CommandLine;
using NeversoftMultitool.Core;
using Spectre.Console;

namespace DdmAnalyzer.Commands;

public static class DumpCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a .PSX file to dump layout data from"
        };

        var command = new Command("dump", "Raw dump of PSX layout data: header, objects, mesh name hashes, tagged chunks");
        command.Arguments.Add(inputArgument);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArgument)!;

            if (!File.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]File not found:[/] {input}");
                return Task.FromResult(1);
            }

            DumpPsxLayout(input);
            return Task.FromResult(0);
        });

        return command;
    }

    private static void DumpPsxLayout(string filePath)
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

            // Header
            var version = reader.ReadUInt16();
            var magic = reader.ReadUInt16();
            AnsiConsole.MarkupLine($"  Version: [cyan]0x{version:X4}[/]  Magic: [cyan]0x{magic:X4}[/]");

            if (version is not (0x03 or 0x04 or 0x06))
            {
                AnsiConsole.MarkupLine($"  [red]Unsupported version (expected 0x03, 0x04, or 0x06)[/]");
                return;
            }
            if (magic != 0x0002)
            {
                AnsiConsole.MarkupLine($"  [red]Invalid magic (expected 0x0002)[/]");
                return;
            }

            var metaTop = reader.ReadUInt32();
            var objectCount = reader.ReadUInt32();
            AnsiConsole.MarkupLine($"  MetaTop: [cyan]0x{metaTop:X8}[/] ({metaTop})");
            AnsiConsole.MarkupLine($"  Object count: [cyan]{objectCount}[/]");

            if (objectCount == 0)
            {
                AnsiConsole.MarkupLine("  [yellow]No objects in file[/]");
                return;
            }

            // Object table
            AnsiConsole.WriteLine();
            var objectTable = new Table()
                .Border(TableBorder.Simple)
                .Title("[bold]Object Table[/]")
                .AddColumn("#", c => c.RightAligned())
                .AddColumn("Flags")
                .AddColumn("MeshIdx", c => c.RightAligned())
                .AddColumn("Raw X", c => c.RightAligned())
                .AddColumn("Raw Y", c => c.RightAligned())
                .AddColumn("Raw Z", c => c.RightAligned())
                .AddColumn("X", c => c.RightAligned())
                .AddColumn("Y", c => c.RightAligned())
                .AddColumn("Z", c => c.RightAligned());

            for (uint i = 0; i < objectCount; i++)
            {
                var flags = reader.ReadUInt32();
                var rawX = reader.ReadInt32();
                var rawY = reader.ReadInt32();
                var rawZ = reader.ReadInt32();
                reader.ReadUInt32(); // unk1
                reader.ReadUInt16(); // unk2
                var meshIndex = reader.ReadUInt16();
                reader.ReadInt16();  // tx
                reader.ReadInt16();  // ty
                reader.ReadUInt32(); // unk3
                reader.ReadUInt32(); // paletteTop

                objectTable.AddRow(
                    i.ToString(),
                    $"0x{flags:X8}",
                    meshIndex.ToString(),
                    rawX.ToString(),
                    rawY.ToString(),
                    rawZ.ToString(),
                    $"{rawX / 4096f:F0}",
                    $"{rawY / 4096f:F0}",
                    $"{rawZ / 4096f:F0}");
            }
            AnsiConsole.Write(objectTable);

            // Mesh count and top pointers
            var meshCount = reader.ReadUInt32();
            AnsiConsole.MarkupLine($"\n  Mesh count: [cyan]{meshCount}[/]");

            if (meshCount == 0) return;

            // Mesh top pointers (offsets into geometry data)
            var meshTops = new uint[meshCount];
            for (uint i = 0; i < meshCount; i++)
                meshTops[i] = reader.ReadUInt32();

            AnsiConsole.MarkupLine("  Mesh top pointers:");
            for (uint i = 0; i < meshCount; i++)
                AnsiConsole.MarkupLine($"    [[{i}]] 0x{meshTops[i]:X8}");

            // Seek to metaTop for tagged chunks
            reader.BaseStream.Seek(metaTop, SeekOrigin.Begin);
            AnsiConsole.MarkupLine($"\n  [bold]Tagged chunks at 0x{metaTop:X8}:[/]");

            const uint TagStop = 0xFFFFFFFF;
            var tag = reader.ReadUInt32();
            while (tag != TagStop)
            {
                var length = reader.ReadUInt32();
                var tagStr = TagToString(tag);
                AnsiConsole.MarkupLine(
                    $"    Tag [cyan]{tagStr}[/] (0x{tag:X8}), length: [cyan]{length}[/] bytes");
                reader.BaseStream.Seek(length, SeekOrigin.Current);
                tag = reader.ReadUInt32();
            }
            AnsiConsole.MarkupLine("    [dim]Stop sentinel (0xFFFFFFFF)[/]");

            // Mesh name hashes
            AnsiConsole.MarkupLine($"\n  [bold]Mesh Name Hashes ({meshCount} entries):[/]");
            var hashTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("#", c => c.RightAligned())
                .AddColumn("Hash")
                .AddColumn("Resolved Name");

            for (uint i = 0; i < meshCount; i++)
            {
                var hash = reader.ReadUInt32();
                var resolved = QbKey.TryResolve(hash);
                hashTable.AddRow(
                    i.ToString(),
                    $"0x{hash:X8}",
                    resolved ?? "[dim]???[/]");
            }
            AnsiConsole.Write(hashTable);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]Error: {ex.Message}[/]");
        }
    }

    private static string TagToString(uint tag)
    {
        var bytes = BitConverter.GetBytes(tag);
        var chars = new char[4];
        for (var i = 0; i < 4; i++)
            chars[i] = bytes[i] is >= 0x20 and <= 0x7E ? (char)bytes[i] : '.';
        return new string(chars);
    }
}
