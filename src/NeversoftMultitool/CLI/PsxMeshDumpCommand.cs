using System.CommandLine;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class PsxMeshDumpCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PSX file"
        };
        var jsonOption = new Option<string>("--json")
        {
            Description = "Output JSON file path"
        };
        jsonOption.Required = true;

        var command = new Command("psx-mesh-dump", "Dump PSX mesh parse diagnostics to JSON");
        command.Arguments.Add(inputArgument);
        command.Options.Add(jsonOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            var input = parseResult.GetValue(inputArgument)!;
            var jsonPath = parseResult.GetValue(jsonOption)!;
            return Task.FromResult(Execute(input, jsonPath));
        });

        return command;
    }

    private static int Execute(string input, string jsonPath)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return 1;
        }

        try
        {
            var snapshot = PsxMeshDumpSnapshotBuilder.Build(input);
            var json = PsxMeshDumpSnapshotBuilder.Serialize(snapshot);

            var directory = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(jsonPath, json);
            AnsiConsole.MarkupLine($"Wrote [green]{snapshot.Meshes.Count}[/] mesh snapshots to [green]{jsonPath}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }
}
