using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Animation;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class SkaCommand
{
    private static readonly string[] SkaSuffixes = [".ska", ".ska.ps2", ".ska.xbx", ".ska.wpc"];

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a SKA animation file or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("ska", "Parse and validate SKA animation files");
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
            files = Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                .Where(static file =>
                {
                    var fileName = Path.GetFileName(file);
                    return OrdinalFileName.HasAnySuffix(fileName, SkaSuffixes)
                           || fileName.EndsWith(".ska", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Input path does not exist[/]");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No SKA files found[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] SKA files");

        var sw = Stopwatch.StartNew();
        var success = 0;
        var failed = 0;
        var totalBones = 0;
        var totalQKeys = 0;
        var totalTKeys = 0;
        var lookupQKeys = 0;
        var lookupTKeys = 0;

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                if (!SkaFile.IsSkaFile(data))
                {
                    if (verbose)
                        AnsiConsole.MarkupLine($"  [grey]{Path.GetFileName(file)}: not a valid SKA file[/]");
                    failed++;
                    continue;
                }

                var anim = SkaFile.Parse(data);
                success++;

                var boneCount = anim.BoneTracks.Length;
                var qCount = anim.BoneTracks.Sum(t => t.RotationKeys.Length);
                var tCount = anim.BoneTracks.Sum(t => t.TranslationKeys.Length);
                totalBones += boneCount;
                totalQKeys += qCount;
                totalTKeys += tCount;

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  [green]{Path.GetFileName(file)}[/]: " +
                        $"v={anim.Version} bones={boneCount} " +
                        $"Q={qCount} T={tCount} dur={anim.Duration:F2}s " +
                        $"flags=0x{anim.Flags:X8}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  [red]{Path.GetFileName(file)}: {ex.Message}[/]");
            }
        }

        sw.Stop();

        AnsiConsole.MarkupLine(
            $"\nParsed [green]{success}[/] animations " +
            $"([red]{failed}[/] failed) in {sw.Elapsed.TotalSeconds:F2}s");
        AnsiConsole.MarkupLine(
            $"Total: {totalQKeys:N0} rotation keys + {totalTKeys:N0} translation keys " +
            $"across {totalBones:N0} bone tracks");

        return failed > 0 ? 1 : 0;
    }
}
