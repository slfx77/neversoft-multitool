using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
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
        var skelOption = new Option<string?>("--ske")
        {
            Description = "Skeleton file (.ske.ps2 or .ske) for glTF export"
        };
        var skinOption = new Option<string?>("--skin")
        {
            Description = "Skin mesh file (.skin.ps2 or .iskin.ps2) for combined mesh+animation glTF"
        };
        var texOption = new Option<string?>("--tex")
        {
            Description = "Texture file (.tex.ps2) for embedding textures in glTF output"
        };

        var command = new Command("ska", "Parse SKA animation files and optionally export to glTF");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(skelOption);
        command.Options.Add(skinOption);
        command.Options.Add(texOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var skePath = parseResult.GetValue(skelOption);
            var skinPath = parseResult.GetValue(skinOption);
            var texPath = parseResult.GetValue(texOption);

            return Task.FromResult(Execute(input, output, verbose, skePath, skinPath, texPath));
        });

        return command;
    }

    private static int Execute(string input, string output, bool verbose, string? skePath,
        string? skinPath, string? texPath)
    {
        // Load skeleton if provided (enables glTF export)
        Ps2Skeleton? skeleton = null;
        if (skePath != null)
        {
            var skelData = File.ReadAllBytes(skePath);
            skeleton = skePath.EndsWith(".ps2", StringComparison.OrdinalIgnoreCase)
                ? Ps2SkeletonFile.Parse(skelData)
                : SkeletonFile.Parse(skelData);
            AnsiConsole.MarkupLine($"Loaded skeleton: [green]{skeleton.Bones.Length}[/] bones from {Path.GetFileName(skePath)}");
        }

        // Load skin mesh if provided (enables combined mesh+animation export)
        Ps2Scene? skinScene = null;
        if (skinPath != null && skeleton != null)
        {
            var skinData = File.ReadAllBytes(skinPath);
            skinScene = Ps2SceneFile.Parse(skinData);
            AnsiConsole.MarkupLine($"Loaded skin: [green]{skinScene.MeshGroups.Sum(g => g.Meshes.Count)}[/] meshes from {Path.GetFileName(skinPath)}");
        }

        // Build texture provider if TEX file provided
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null;
        if (texPath != null)
        {
            var textureCache = Ps2TextureLoader.BuildTextureCache([], texPath, verbose);
            if (textureCache.Count > 0)
            {
                AnsiConsole.MarkupLine($"Loaded [green]{textureCache.Count}[/] textures from {Path.GetFileName(texPath)}");
                textureProvider = checksum =>
                {
                    if (!textureCache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                        return null;
                    return Core.BinaryIO.ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
                };
            }
        }

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

                var compressTable = FindCompressTable(file);
                var anim = SkaFile.Parse(data, compressTable);
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

                // Export to glTF if skeleton is available and bone counts match
                if (skeleton != null && boneCount == skeleton.Bones.Length)
                {
                    var stem = Path.GetFileNameWithoutExtension(
                        Path.GetFileNameWithoutExtension(file)); // strip .ska.ps2
                    var glbPath = Path.Combine(output, stem + ".glb");

                    if (skinScene != null)
                    {
                        // Combined mesh + animation
                        var tris = Ps2SceneGltfWriter.WriteSkinnedAnimated(
                            skinScene, skeleton, anim, glbPath, stem,
                            textureProvider);
                        if (verbose)
                            AnsiConsole.MarkupLine($"    → [blue]{glbPath}[/] (skinned, {tris} triangles)");
                    }
                    else
                    {
                        // Skeleton-only animation
                        var channels = SkaGltfWriter.WriteAnimatedSkeleton(
                            skeleton, anim, glbPath, stem);
                        if (verbose)
                            AnsiConsole.MarkupLine($"    → [blue]{glbPath}[/] ({channels} channels)");
                    }
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

    // Q48/T48 compression tables (standardkeyQ.bin / standardkeyT.bin) are required to
    // decode quantised rotation/translation keys. Without them, table-lookup keys fall
    // back to identity, causing visible "snap to bind pose" stutter at those frames.
    // Cache by directory since tables apply to every SKA in the same build.
    private static readonly Dictionary<string, SkaCompressTable?> _tableCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static SkaCompressTable? FindCompressTable(string skaFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(skaFilePath));
        while (!string.IsNullOrEmpty(dir))
        {
            if (_tableCache.TryGetValue(dir, out var cached))
                return cached;

            // Standard locations: ../BIN/standardkey{Q,T}.bin (THPS4/THUG/THUG2/THAW PS2),
            // ../anims/standardkey{Q,T}.bin (THAW GC).
            foreach (var subdir in new[] { "BIN", "bin", "anims" })
            {
                var qPath = Path.Combine(dir, subdir, "standardkeyQ.bin");
                var tPath = Path.Combine(dir, subdir, "standardkeyT.bin");
                if (!File.Exists(qPath))
                {
                    qPath = Path.Combine(dir, subdir, "standardkeyq.bin");
                    tPath = Path.Combine(dir, subdir, "standardkeyt.bin");
                }
                if (File.Exists(qPath) && File.Exists(tPath))
                {
                    var table = SkaCompressTable.TryLoad(qPath, tPath);
                    _tableCache[dir] = table;
                    return table;
                }
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
