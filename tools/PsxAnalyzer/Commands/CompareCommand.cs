using System.CommandLine;
using NeversoftMultitool.Core.Formats.Psx;
using Spectre.Console;

namespace PsxAnalyzer.Commands;

public static class CompareCommand
{
    public static Command Create()
    {
        var dir1Argument = new Argument<string>("dir1")
        {
            Description = "First directory containing .psx files (e.g. PS1 or DC build)"
        };
        var dir2Argument = new Argument<string>("dir2")
        {
            Description = "Second directory containing .psx files (e.g. Xbox build)"
        };

        var command = new Command("compare", "Compare PSX file structures across two directories (e.g. PS1 vs Xbox)");
        command.Arguments.Add(dir1Argument);
        command.Arguments.Add(dir2Argument);

        command.SetAction((parseResult, _) =>
        {
            var dir1 = parseResult.GetValue(dir1Argument)!;
            var dir2 = parseResult.GetValue(dir2Argument)!;

            if (!Directory.Exists(dir1) || !Directory.Exists(dir2))
            {
                AnsiConsole.MarkupLine("[red]Both directories must exist.[/]");
                return Task.FromResult(1);
            }

            var files1 = Directory.GetFiles(dir1, "*.psx")
                .ToDictionary(f => Path.GetFileName(f).ToLowerInvariant(), f => f);
            var files2 = Directory.GetFiles(dir2, "*.psx")
                .ToDictionary(f => Path.GetFileName(f).ToLowerInvariant(), f => f);

            var allNames = files1.Keys.Union(files2.Keys).OrderBy(n => n).ToList();
            AnsiConsole.MarkupLine($"Dir1: [cyan]{dir1}[/] ({files1.Count} files)");
            AnsiConsole.MarkupLine($"Dir2: [cyan]{dir2}[/] ({files2.Count} files)");
            AnsiConsole.MarkupLine($"Unique filenames: [cyan]{allNames.Count}[/]");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("File");
            table.AddColumn("Dir1 Magic");
            table.AddColumn("Dir1 TexCount");
            table.AddColumn("Dir2 Magic");
            table.AddColumn("Dir2 TexCount");

            foreach (var name in allNames)
            {
                var (magic1, texCount1) = files1.TryGetValue(name, out var path1) ? GetQuickInfo(path1) : ("—", "—");
                var (magic2, texCount2) = files2.TryGetValue(name, out var path2) ? GetQuickInfo(path2) : ("—", "—");

                var texCountColor2 = texCount2 == "0xFFFFFFFF" ? "[yellow]" : "[green]";

                table.AddRow(
                    name,
                    magic1,
                    texCount1,
                    magic2,
                    $"{texCountColor2}{texCount2}[/]"
                );
            }

            AnsiConsole.Write(table);
            return Task.FromResult(0);
        });

        return command;
    }

    private static (string magic, string texCount) GetQuickInfo(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            var magic = reader.ReadBytes(4);
            var magicStr = $"{magic[0]:X2}{magic[1]:X2}{magic[2]:X2}{magic[3]:X2}";

            if (!PsxLibrary.IsValidMagic(magic))
                return (magicStr, "invalid");

            PsxLibrary.SkipModelData(reader);
            PsxLibrary.ReadTextureInfo(reader);
            PsxLibrary.ReadPalettes(reader, 16);
            PsxLibrary.ReadPalettes(reader, 256);

            var numActualTex = reader.ReadUInt32();
            var texCountStr = numActualTex == 0xFFFFFFFF ? "0xFFFFFFFF" : numActualTex.ToString();

            return (magicStr, texCountStr);
        }
        catch (Exception ex)
        {
            return ("error", ex.Message.Length > 30 ? ex.Message[..30] + "..." : ex.Message);
        }
    }
}
