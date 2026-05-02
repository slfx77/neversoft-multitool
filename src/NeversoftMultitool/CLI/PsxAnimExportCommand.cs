using System.CommandLine;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Single-file CLI: parse a PSX character, decode its embedded animations,
///     and emit an animated <c>.glb</c> via <see cref="PsxGltfWriter.WriteAnimated" />.
/// </summary>
public static class PsxAnimExportCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PSX character file"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output .glb path (default: {stem}_animated.glb next to input)"
        };
        var animOption = new Option<int>("--anim")
        {
            Description = "Animation slot to embed (default: -1 = all)",
            DefaultValueFactory = _ => -1
        };
        var fpsOption = new Option<float>("--fps")
        {
            Description = "Frame rate for time-base conversion (default: 30)",
            DefaultValueFactory = _ => 30f
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "Override the default anim_N name (only valid with --anim N)"
        };
        var noRotOption = new Option<bool>("--no-rot")
        {
            Description = "Diagnostic: skip rotation tracks (bones keep bind rotation)"
        };
        var noTransOption = new Option<bool>("--no-trans")
        {
            Description = "Diagnostic: skip translation tracks (bones keep bind translation)"
        };
        var rotComposeOption = new Option<string>("--rot-compose")
        {
            Description = "Quaternion composition order: yxz (default), zxy, xyz, zyx",
            DefaultValueFactory = _ => "yxz"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("psx-anim-export",
            "Export a PS1 character .psx as an animated .glb (one or all embedded animations)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(animOption);
        command.Options.Add(fpsOption);
        command.Options.Add(nameOption);
        command.Options.Add(noRotOption);
        command.Options.Add(noTransOption);
        command.Options.Add(rotComposeOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var anim = parseResult.GetValue(animOption);
            var fps = parseResult.GetValue(fpsOption);
            var name = parseResult.GetValue(nameOption);
            var noRot = parseResult.GetValue(noRotOption);
            var noTrans = parseResult.GetValue(noTransOption);
            var rotCompose = parseResult.GetValue(rotComposeOption);
            var verbose = parseResult.GetValue(verboseOption);
            var opts = new PsxAnimationOptions(
                SkipRotation: noRot,
                SkipTranslation: noTrans,
                RotationCompose: ParseRotCompose(rotCompose),
                Fps: fps);
            return Task.FromResult(Execute(input, output, anim, name, opts, verbose));
        });

        return command;
    }

    private static PsxRotationCompose ParseRotCompose(string s)
    {
        if (Enum.TryParse<PsxRotationCompose>(s, ignoreCase: true, out var compose))
            return compose;
        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] Unknown --rot-compose value '{Markup.Escape(s)}'; using YXZ.");
        return PsxRotationCompose.YXZ;
    }

    private static int Execute(
        string input, string? output, int animIndex, string? animName,
        PsxAnimationOptions opts, bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return 1;
        }

        var data = File.ReadAllBytes(input);
        var psxFile = PsxMeshFile.Parse(data);
        if (psxFile == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] PSX file has no parseable mesh data.");
            return 1;
        }

        if (!psxFile.HasHierarchy)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] PSX file is not a hierarchical character — " +
                "animations are only valid for character models.");
        }

        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd);
        if (animFile == null || animFile.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No recognizable animation table in this PSX file.");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Layout:[/] {animFile.Layout}  numStreams={animFile.NumStreamsDeclared}  " +
            $"recoverable={animFile.Entries.Count}  bones={psxFile.Objects.Count}");
        if (animFile.Layout != PsxAnimLayoutVariant.Monolithic)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Note:[/] best-effort layout — only the first animation may decode cleanly.");
        }

        var selected = ResolveSelectedEntries(animFile, animIndex, animName);
        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Animation index {animIndex} out of range.");
            return 1;
        }

        var decoded = DecodeAnimations(animFile, psxFile.Objects.Count, selected, verbose);
        if (decoded.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No animations decoded successfully.");
            return 1;
        }

        var pshFile = PshFile.FindCompanion(input);
        var textureProvider = PsxTextureProviderFactory.FromFile(input);
        var outputPath = output ?? DeriveOutputPath(input);

        var triangles = PsxGltfWriter.WriteAnimated(
            psxFile, decoded, outputPath, textureProvider, pshFile, opts);

        AnsiConsole.MarkupLine(
            $"[green]Wrote[/] {Markup.Escape(outputPath)}  " +
            $"triangles={triangles:N0}  animations={decoded.Count}  fps={opts.Fps:F1}  " +
            $"compose={opts.RotationCompose}  rot={(opts.SkipRotation ? "off" : "on")}  " +
            $"trans={(opts.SkipTranslation ? "off" : "on")}");

        return triangles == 0 ? 1 : 0;
    }

    private static List<(int Index, string Name)> ResolveSelectedEntries(
        PsxAnimFile animFile, int animIndex, string? animName)
    {
        if (animIndex < 0)
        {
            // All entries; ignore --name (only meaningful per-anim).
            return Enumerable.Range(0, animFile.Entries.Count)
                .Select(i => (i, $"anim_{i}"))
                .ToList();
        }

        if (animIndex >= animFile.Entries.Count)
            return [];

        var name = string.IsNullOrWhiteSpace(animName) ? $"anim_{animIndex}" : animName;
        return [(animIndex, name)];
    }

    private static List<(string Name, PsxAnimation Animation)> DecodeAnimations(
        PsxAnimFile animFile, int boneCount,
        List<(int Index, string Name)> selected, bool verbose)
    {
        var decoded = new List<(string, PsxAnimation)>(selected.Count);
        foreach (var (index, name) in selected)
        {
            var entry = animFile.Entries[index];
            try
            {
                var slice = animFile.Pool.Span[entry.PoolOffset..];
                var animation = PsxAnimDecoder.Decode(
                    slice, boneCount, entry.FrameCount, out var consumed);
                decoded.Add((name, animation));
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  [grey]{name,-12}[/] frames={entry.FrameCount,4}  " +
                        $"poolOffset=+0x{entry.PoolOffset:X6}  bytesConsumed={consumed,5}");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]{name}: decode failed ({Markup.Escape(ex.Message)})[/]");
            }
        }
        return decoded;
    }

    private static string DeriveOutputPath(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(dir, stem + "_animated.glb");
    }
}
