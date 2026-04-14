using System.CommandLine;
using Spectre.Console;
using static PsxAnalyzer.Utils.BinaryHelpers;

namespace PsxAnalyzer.Commands;

internal static class HexCommand
{
    public static Command Create()
    {
        var command = new Command("hex", "Dump raw hex bytes at a specific offset in a file");
        var fileArg = new Argument<string>("file") { Description = "File path" };
        var offsetArg = new Argument<long>("offset") { Description = "Byte offset to start dump" };
        var lengthArg = new Argument<int>("length") { Description = "Number of bytes to dump (default: 256)", DefaultValueFactory = _ => 256 };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(offsetArg);
        command.Arguments.Add(lengthArg);
        command.SetAction(parseResult => Hex(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(offsetArg),
            parseResult.GetValue(lengthArg)));
        return command;
    }

    private static void Hex(string path, long offset, int length)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {path}");
            return;
        }

        var data = File.ReadAllBytes(path);

        if (offset < 0 || offset >= data.Length)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Offset 0x{offset:X} out of range (file size: {data.Length})");
            return;
        }

        length = (int)Math.Min(length, data.Length - offset);

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("Value");
        infoTable.AddRow("File", Markup.Escape(Path.GetFileName(path)));
        infoTable.AddRow("File Size", $"{data.Length:N0} bytes");
        infoTable.AddRow("Offset", $"0x{offset:X4} ({offset})");
        infoTable.AddRow("Length", $"{length} bytes");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        HexDump(data, (int)offset, length);
    }
}
