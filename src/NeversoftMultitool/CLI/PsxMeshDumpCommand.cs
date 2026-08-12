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
            var input = parseResult.GetValue(inputArgument)!;
            var jsonPath = parseResult.GetValue(jsonOption)!;
            return Task.FromResult(Execute(input, jsonPath, cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        try
        {
            var snapshot = PsxMeshDumpSnapshotBuilder.Build(input);
            cancellationToken.ThrowIfCancellationRequested();
            var json = PsxMeshDumpSnapshotBuilder.Serialize(snapshot);
            cancellationToken.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(jsonPath, json);
            AnsiConsole.MarkupLine(
                $"Wrote [green]{snapshot.Meshes.Count}[/] mesh snapshots to [green]{Markup.Escape(jsonPath)}[/]");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
