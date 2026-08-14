using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
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

            return Task.FromResult(Execute(input, output, verbose, cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<string> files;
        string? inputRoot = null;
        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            inputRoot = Path.GetFullPath(input);
            files = Directory.GetFiles(inputRoot, "*.*", SearchOption.AllDirectories)
                .Where(static path => OrdinalFileName.HasAnySuffix(Path.GetFileName(path), SupportedSuffixes))
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {Markup.Escape(input)}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No N64 texture records found.[/]");
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Processing [green]{files.Count}[/] N64 texture record(s)");

        var stopwatch = Stopwatch.StartNew();
        var failed = 0;
        var decoded = new List<DecodedTextureCandidate>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                decoded.Add(new DecodedTextureCandidate(
                    file,
                    N64TexFile.Decode(File.ReadAllBytes(file))));
            }
            catch (Exception ex) when (ex is InvalidDataException
                                           or IndexOutOfRangeException
                                           or ArgumentOutOfRangeException)
            {
                failed++;
                AnsiConsole.MarkupLine(
                    $"  {Markup.Escape(Path.GetFileName(file))}: [red]{Markup.Escape(ex.Message)}[/]");
            }
        }

        var decodedByFile = decoded.ToDictionary(
            static candidate => candidate.File,
            StringComparer.Ordinal);
        var outputPlan = MeshOutputPathPlanner.Plan(
            [.. decoded.Select(static candidate => candidate.File)],
            N64TextureOutput.GetLegacyOutputStem,
            (file, proposedStem) => GetOutputStems(
                decodedByFile[file].Texture,
                proposedStem),
            inputRoot);

        var converted = 0;
        foreach (var planned in outputPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decodedTexture = decodedByFile[planned.File].Texture;
            var plannedOutput = string.IsNullOrEmpty(planned.Subdirectory)
                ? output
                : Path.Combine(output, planned.Subdirectory);
            var paths = N64TextureOutput.WritePngLevels(
                decodedTexture,
                Path.Combine(plannedOutput, $"{planned.Stem}.png"));
            converted++;
            if (verbose)
            {
                var mipSuffix = paths.Count > 1
                    ? $" (+{paths.Count - 1} stored mip level(s))"
                    : "";
                AnsiConsole.MarkupLine(
                    $"  {Markup.Escape(Path.GetFileName(planned.File))} -> " +
                    $"[green]{Markup.Escape(Path.GetFileName(paths[0]))}[/]{mipSuffix}");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} records ({failed} failed) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");
        return failed == 0 ? 0 : 1;
    }

    private static List<string> GetOutputStems(
        N64TexFile.N64Texture texture,
        string proposedStem)
    {
        var outputStems = new List<string> { proposedStem };
        outputStems.AddRange(texture.MipLevels
            .Where(static level => level.Level > 0)
            .OrderBy(static level => level.Level)
            .Select(level => N64TextureOutput.BuildMipStem(
                proposedStem,
                level.Level)));
        return outputStems;
    }

    private readonly record struct DecodedTextureCandidate(
        string File,
        N64TexFile.N64Texture Texture);
}
