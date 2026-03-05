using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.XbxScene;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class XbxSceneCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to an Xbox/PC scene file (.skin.xbx, .mdl.xbx) or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for .glb files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var texturesOption = new Option<bool>("-t", "--textures")
        {
            Description = "Embed textures from companion .tex.xbx files"
        };
        var texPathOption = new Option<string?>("--tex")
        {
            Description = "Explicit TEX file or directory to use for texture lookup"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("xbxscene", "Convert Xbox/PC scene files (SKIN/MDL) to glTF (.glb) — THUG2 + THAW");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texturesOption);
        command.Options.Add(texPathOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var textures = parseResult.GetValue(texturesOption);
            var texPath = parseResult.GetValue(texPathOption);
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, textures, texPath, verbose));
        });

        return command;
    }

    private static int Execute(string input, string output, bool embedTextures,
        string? texPath, bool verbose)
    {
        var files = CollectFiles(input);
        if (files == null) return 1;
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Xbox scene files found.[/]");
            return 0;
        }

        var textureProvider = BuildTextureProvider(files, embedTextures, texPath, verbose);

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] Xbox scene file(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var totalTriangles = 0;
        var texturedCount = 0;

        foreach (var file in files)
        {
            var (tris, textured, success) = ConvertFile(file, output, textureProvider, verbose);
            if (success) { converted++; totalTriangles += tris; if (textured) texturedCount++; }
            else { failed++; }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"\nConverted [green]{converted}[/] files " +
            $"({totalTriangles:N0} triangles, {texturedCount} textured) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F1}s");

        if (failed > 0)
            AnsiConsole.MarkupLine($"[red]{failed} file(s) failed[/]");

        return failed > 0 ? 1 : 0;
    }

    private static List<string>? CollectFiles(string input)
    {
        if (File.Exists(input))
            return [input];

        if (Directory.Exists(input))
        {
            var allExts = XbxSceneFile.SupportedExtensions
                .Concat(ThawSceneFile.SupportedExtensions)
                .Distinct()
                .ToArray();
            return Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(p => allExts.Any(
                    ext => Path.GetFileName(p).EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
        return null;
    }

    private static XbxSceneGltfWriter.TextureProvider? BuildTextureProvider(
        List<string> files, bool embedTextures, string? texPath, bool verbose)
    {
        if (!embedTextures && texPath == null) return null;

        var texCache = XbxTextureLoader.BuildTextureCache(files, texPath, verbose);
        if (texCache.Count == 0) return null;

        AnsiConsole.MarkupLine($"Loaded [green]{texCache.Count}[/] textures for embedding");
        return checksum =>
        {
            if (!texCache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                return null;
            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
        };
    }

    private static (int triangles, bool textured, bool success) ConvertFile(
        string file, string output, XbxSceneGltfWriter.TextureProvider? textureProvider, bool verbose)
    {
        var filename = Path.GetFileName(file);
        var allExts = XbxSceneFile.SupportedExtensions
            .Concat(ThawSceneFile.SupportedExtensions)
            .Distinct();
        var matchedExt = allExts
            .FirstOrDefault(ext => filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var stem = matchedExt != null ? filename[..^matchedExt.Length] : filename;

        try
        {
            var fileData = File.ReadAllBytes(file);
            var scene = ThawSceneFile.IsThawScene(fileData)
                ? ThawSceneFile.Parse(fileData)
                : XbxSceneFile.Parse(fileData);
            var outputPath = Path.Combine(output, stem + ".glb");
            var triangles = XbxSceneGltfWriter.Write(scene, outputPath, textureProvider);
            var textured = scene.Materials.Any(m => m.Passes.Length > 0 && m.Passes[0].TextureChecksum != 0);

            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  [green]\u2713[/] {Markup.Escape(filename)}: " +
                    $"{scene.Sectors.Length} sectors, {scene.TotalVertices} verts, " +
                    $"{triangles} tris, {scene.Materials.Length} mats");
            }

            return (triangles, textured, true);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"  [red]\u2717[/] {Markup.Escape(filename)}: {Markup.Escape(ex.Message)}");
            return (0, false, false);
        }
    }
}
