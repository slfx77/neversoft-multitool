using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Font;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Converts PS1-era Neversoft <c>.fnt</c> bitmap fonts to a packed PNG glyph atlas plus a
///     schema-v1 metrics manifest.
/// </summary>
/// <remarks>
///     <c>.fnt</c> is a generic extension and the corpus proves it: THAW and THPS3-PS2 ship
///     unrelated formats under it. Files that fail the strict structural gate are therefore
///     reported as skipped rather than counted as conversion errors.
/// </remarks>
public static class FntCommand
{
    /// <summary>Widest sheet the packer will allocate; the widest corpus glyph is 40 pixels.</summary>
    private const int MaxAtlasWidth = 16384;

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a Neversoft .fnt bitmap font or a directory to search"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for the glyph atlas and metrics JSON",
            DefaultValueFactory = _ => "TestOutput"
        };
        var charMapOption = new Option<int?>("--charmap")
        {
            Description =
                "Label glyphs using Font::CharMap mode 0 (standard), 1 (numeric), or 2 " +
                "(lowercase-first). The mode is runtime state the file does not record, so " +
                "glyphs stay unlabelled unless you state it"
        };
        var atlasWidthOption = new Option<int>("--atlas-width")
        {
            Description = "Width the atlas packer wraps at, in pixels",
            DefaultValueFactory = _ => FntAtlasBuilder.DefaultMaxWidth
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show per-file layout, glyph count, and atlas size"
        };

        var command = new Command(
            "fnt",
            "Convert Neversoft bitmap fonts (.fnt) to a PNG glyph atlas plus metrics JSON");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(charMapOption);
        command.Options.Add(atlasWidthOption);
        command.Options.Add(verboseOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(charMapOption),
                parseResult.GetValue(atlasWidthOption),
                parseResult.GetValue(verboseOption),
                cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        string output,
        int? charMap,
        int atlasWidth,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryValidateArguments(input, charMap, atlasWidth, out var characterMapMode))
            return 1;

        var files = FindFiles(input);
        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .fnt files found.[/]");
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var skipped = 0;
        var errors = 0;
        var totalGlyphs = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (ConvertOne(input, file, output, characterMapMode, atlasWidth, verbose, out var glyphs))
            {
                case ConversionOutcome.Converted:
                    converted++;
                    totalGlyphs += glyphs;
                    break;
                case ConversionOutcome.NotThisFormat:
                    skipped++;
                    break;
                default:
                    errors++;
                    break;
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Length} bitmap fonts " +
            $"({totalGlyphs} glyphs) in {stopwatch.Elapsed.TotalSeconds:F2}s" +
            (skipped == 0 ? string.Empty : $" ([yellow]{skipped} not this format[/])") +
            (errors == 0 ? string.Empty : $" ([red]{errors} errors[/])"));
        return errors == 0 ? 0 : 1;
    }

    /// <summary>Outcome of converting one candidate file.</summary>
    private enum ConversionOutcome
    {
        Converted,

        /// <summary>Structurally not a Neversoft bitmap font — counted, but not an error.</summary>
        NotThisFormat,

        Failed
    }

    private static bool TryValidateArguments(
        string input,
        int? charMap,
        int atlasWidth,
        out FntCharacterMap.Mode? characterMapMode)
    {
        characterMapMode = null;

        if (!File.Exists(input) && !Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Input does not exist: {Markup.Escape(input)}[/]");
            return false;
        }

        if (charMap is not (null or 0 or 1 or 2))
        {
            AnsiConsole.MarkupLine($"[red]Unsupported --charmap value {charMap}; expected 0, 1, or 2.[/]");
            return false;
        }

        // Bounded rather than merely positive: the sheet allocates width * height * 4 bytes, so
        // an absurd width would fail as an out-of-memory surprise instead of a clear message.
        if (atlasWidth is <= 0 or > MaxAtlasWidth)
        {
            AnsiConsole.MarkupLine(
                $"[red]--atlas-width must be between 1 and {MaxAtlasWidth}, got {atlasWidth}.[/]");
            return false;
        }

        characterMapMode = charMap is { } mode ? (FntCharacterMap.Mode)mode : null;
        return true;
    }

    private static ConversionOutcome ConvertOne(
        string inputRoot,
        string file,
        string outputRoot,
        FntCharacterMap.Mode? characterMapMode,
        int atlasWidth,
        bool verbose,
        out int glyphCount)
    {
        glyphCount = 0;
        try
        {
            if (!FntFile.TryParse(File.ReadAllBytes(file), out var font))
            {
                // Skips are expected in bulk — 60 of the 443 corpus .fnt files are other
                // formats — so the summary carries the count and only -v names them.
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  [yellow]{Markup.Escape(Path.GetFileName(file))}: " +
                        $"not a Neversoft bitmap font[/]");
                }

                return ConversionOutcome.NotThisFormat;
            }

            var (directory, stem) = GetOutputTarget(inputRoot, file, outputRoot);
            var written = FntOutput.Write(directory, stem, file, font, characterMapMode, atlasWidth);
            glyphCount = font.Glyphs.Length;

            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  [green]{Markup.Escape(Path.GetFileName(file))}[/]: " +
                    $"layout={font.Layout} glyphs={font.Glyphs.Length} " +
                    $"palette={font.Palette.Length} -> {Markup.Escape(Path.GetFileName(written[0]))}");
            }

            return ConversionOutcome.Converted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"  [red]{Markup.Escape(Path.GetFileName(file))}: {Markup.Escape(ex.Message)}[/]");
            return ConversionOutcome.Failed;
        }
    }

    private static string[] FindFiles(string input)
    {
        if (File.Exists(input))
            return [input];
        if (!Directory.Exists(input))
            return [];

        return Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
            .Where(static file => Path.GetExtension(file)
                .Equals(".fnt", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    ///     Resolves the output directory and shared stem for one font, mirroring the input's
    ///     relative directory so the many same-named fonts across builds cannot overwrite
    ///     each other.
    /// </summary>
    internal static (string Directory, string Stem) GetOutputTarget(
        string inputRoot,
        string filePath,
        string outputRoot)
    {
        string relativePath;
        if (Directory.Exists(inputRoot))
        {
            relativePath = Path.GetRelativePath(
                Path.GetFullPath(inputRoot),
                Path.GetFullPath(filePath));
            if (Path.IsPathRooted(relativePath)
                || relativePath.Equals("..", StringComparison.Ordinal)
                || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Bitmap font input '{filePath}' is outside input root '{inputRoot}'");
            }
        }
        else
        {
            relativePath = Path.GetFileName(filePath);
        }

        var relativeDirectory = Path.GetDirectoryName(relativePath);
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        var directory = string.IsNullOrEmpty(relativeDirectory)
            ? outputRoot
            : Path.Combine(outputRoot, relativeDirectory);

        var outputRelative = Path.GetRelativePath(
            Path.GetFullPath(outputRoot),
            Path.GetFullPath(Path.Combine(directory, stem)));
        if (Path.IsPathRooted(outputRelative)
            || outputRelative.Equals("..", StringComparison.Ordinal)
            || outputRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || outputRelative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Bitmap font output '{directory}' escapes output root '{outputRoot}'");
        }

        return (directory, stem);
    }
}
