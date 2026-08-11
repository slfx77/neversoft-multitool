using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Texture.N64;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Converts carved N64 texture records (.tex.n64 dictionary entries,
///     .img.n64 fullscreen image records — produced by extracting a .z64 via
///     the archive/unpack commands) to PNG, including complete stored mip
///     levels when present.
/// </summary>
public static class N64TexCommand
{
    private static readonly string[] SupportedSuffixes = [".tex.n64", ".img.n64"];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to an N64 texture record (.tex.n64/.img.n64) or directory containing them"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for converted PNG files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command(
            "n64tex",
            "Convert carved N64 texture records and their stored mip levels to PNG");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, verbose));
        });

        return command;
    }

    private static int Execute(string input, string output, bool verbose)
    {
        List<string> files;
        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(static path => OrdinalFileName.HasAnySuffix(Path.GetFileName(path), SupportedSuffixes))
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No N64 texture records found.[/]");
            return 0;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Processing [green]{files.Count}[/] N64 texture record(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        foreach (var file in files)
        {
            try
            {
                var paths = N64TextureOutput.ConvertToPngLevels(file, output);
                converted++;
                if (verbose)
                {
                    var mipSuffix = paths.Count > 1
                        ? $" (+{paths.Count - 1} stored mip level(s))"
                        : "";
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(Path.GetFileName(file))} -> " +
                        $"[green]{Markup.Escape(Path.GetFileName(paths[0]))}[/]{mipSuffix}");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                failed++;
                AnsiConsole.MarkupLine(
                    $"  {Markup.Escape(Path.GetFileName(file))}: [red]{Markup.Escape(ex.Message)}[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} records ({failed} failed) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");
        return failed == 0 ? 0 : 1;
    }
}
