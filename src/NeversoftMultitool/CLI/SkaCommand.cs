using System.CommandLine;
using System.Diagnostics;
using System.Numerics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class SkaCommand
{
    // .ska.xen / .ska.ps3 cover THAW's ordinary big-endian v0x28 files and
    // Project 8 / Proving Ground's later 0x20-wrapped, section-addressed
    // v0x28/v0x48 payloads. Keep the compound suffixes in both explicit-file
    // and recursive-directory routes.
    private static readonly string[] SkaSuffixes =
        [".ska", ".ska.ps2", ".ska.xbx", ".ska.wpc", ".ska.ngc", ".ska.xen", ".ska.ps3"];

    // Q48/T48 compression tables (standardkeyQ.bin / standardkeyT.bin) are required to
    // decode quantised rotation/translation lookup keys. Without them, shared compressed
    // lookup records fail closed instead of exporting fabricated identity/zero tracks.
    // Cache by directory since tables apply to every SKA in the same build.
    private static readonly Dictionary<string, SkaCompressTable?> _tableCache =
        new(StringComparer.OrdinalIgnoreCase);

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
            Description = "Skeleton file (.ske.ps2 or .ske) for animation export"
        };
        var animationSkelOption = new Option<string?>("--animation-ske")
        {
            Description = "Skeleton whose bone order the SKA tracks use; requires --ske and binds by exact QbKey"
        };
        var skinOption = new Option<string?>("--skin")
        {
            Description = "Skin mesh file (.skin.ps2 or .iskin.ps2) for combined mesh+animation export"
        };
        var texOption = new Option<string?>("--tex")
        {
            Description = "Texture file (.tex.ps2) for embedding textures in export output"
        };
        var sknOption = new Option<string?>("--skn")
        {
            Description = "RenderWare DFF file (.SKN) for THPS3 PS2 skeleton + mesh"
        };
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("ska", "Parse SKA animation files and optionally export to glTF or Blender");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(skelOption);
        command.Options.Add(animationSkelOption);
        command.Options.Add(skinOption);
        command.Options.Add(texOption);
        command.Options.Add(sknOption);
        command.Options.Add(formatOption);
        command.Options.Add(blenderHelperOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var skePath = parseResult.GetValue(skelOption);
            var animationSkePath = parseResult.GetValue(animationSkelOption);
            var skinPath = parseResult.GetValue(skinOption);
            var texPath = parseResult.GetValue(texOption);
            var sknPath = parseResult.GetValue(sknOption);
            if (!MeshExportCliOptions.ValidateFormat(parseResult.GetValue(formatOption), out var format))
                return Task.FromResult(1);
            var blenderHelperPath = parseResult.GetValue(blenderHelperOption);

            return Task.FromResult(Execute(
                input, output, verbose, skePath, skinPath, texPath, sknPath, animationSkePath,
                format, blenderHelperPath, cancellationToken));
        });

        return command;
    }

    internal static int Execute(string input, string output, bool verbose, string? skePath,
        string? skinPath, string? texPath, string? sknPath, string? animationSkePath = null,
        MeshOutputFormat format = MeshOutputFormat.Glb, string? blenderHelperPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (animationSkePath != null && skePath == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --animation-ske requires --ske as the target skeleton");
            return 1;
        }

        if (animationSkePath != null && sknPath != null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --animation-ske cannot be combined with --skn");
            return 1;
        }

        // Load skeleton if provided (enables 3D animation export).
        Ps2Skeleton? skeleton = null;
        if (skePath != null)
        {
            skeleton = LoadSkeleton(skePath);
            AnsiConsole.MarkupLine(
                $"Loaded skeleton: [green]{skeleton.Bones.Length}[/] bones from " +
                Markup.Escape(Path.GetFileName(skePath)));
        }

        Ps2Skeleton? animationSkeleton = null;
        SkaQbKeyBoneMap? qbKeyBoneMap = null;
        if (animationSkePath != null)
        {
            animationSkeleton = LoadSkeleton(animationSkePath);
            qbKeyBoneMap = SkaQbKeyBoneMap.Create(animationSkeleton, skeleton!);
            AnsiConsole.MarkupLine(
                $"Loaded animation skeleton: [green]{animationSkeleton.Bones.Length}[/] bones from " +
                $"{Markup.Escape(Path.GetFileName(animationSkePath))}; exact QbKey map binds " +
                $"[green]{qbKeyBoneMap.MappedBoneCount}[/] and skips " +
                $"[yellow]{qbKeyBoneMap.SourceBoneCount - qbKeyBoneMap.MappedBoneCount}[/]");
        }

        // Load skin mesh if provided (enables combined mesh+animation export)
        Ps2Scene? skinScene = null;
        if (skinPath != null && skeleton != null)
        {
            var skinData = File.ReadAllBytes(skinPath);
            skinScene = Ps2SceneFile.Parse(skinData);
            AnsiConsole.MarkupLine(
                $"Loaded skin: [green]{skinScene.MeshGroups.Sum(g => g.Meshes.Count)}[/] meshes from " +
                Markup.Escape(Path.GetFileName(skinPath)));
        }

        // Load THPS3 RW DFF .SKN (has embedded skeleton + skinned mesh in one file).
        // Texture discovery happens inside MeshModelParser when the parser is invoked
        // per-animation; this block just emits the informational summary up-front.
        RwDffClump? rwClump = null;
        if (sknPath != null)
        {
            var sknData = File.ReadAllBytes(sknPath);
            rwClump = RwDffFile.Parse(sknData);
            var rwBoneCount = rwClump.Atomics
                .Select(a => a.SkinData?.NumBones ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            AnsiConsole.MarkupLine(
                $"Loaded RW DFF: [green]{rwClump.Geometries.Length}[/] geometries, " +
                $"[green]{rwBoneCount}[/] bones from {Markup.Escape(Path.GetFileName(sknPath))}");
        }

        // Texture summary (the per-anim parser handles actual embedding).
        if (texPath != null)
        {
            var textureCache = Ps2TextureLoader.BuildTextureCache([], texPath, verbose);
            if (textureCache.Count > 0)
                AnsiConsole.MarkupLine(
                    $"Loaded [green]{textureCache.Count}[/] textures from " +
                    Markup.Escape(Path.GetFileName(texPath)));
        }

        List<string> files;
        string? inputDirectoryRoot = null;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            inputDirectoryRoot = Path.GetFullPath(input);
            files = Directory.GetFiles(inputDirectoryRoot, "*", SearchOption.AllDirectories)
                .Where(AnimationDiscovery.IsAnimFileName)
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

        // THPS4 V1 skeletons have no bind-pose data in the .ske file — the engine
        // loaded it from a "default animation" per archetype. Try to find + apply it.
        if (skeleton is { Version: 1 } && skePath != null)
        {
            var defaultSkaPath = FindDefaultPoseFile(skePath, files[0]);
            if (defaultSkaPath != null)
            {
                var defaultData = File.ReadAllBytes(defaultSkaPath);
                if (SkaFile.IsSkaFile(defaultData))
                {
                    var table = FindCompressTable(defaultSkaPath);
                    var defaultAnim = Thps4PcDatAnimationFile.IsCandidateFileName(defaultSkaPath)
                        ? Thps4PcDatAnimationFile.ParseExact(defaultData, table)
                        : SkaFile.Parse(defaultData, table);
                    if (!IsUsableDefaultPose(defaultAnim))
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]Found default anim {Markup.Escape(Path.GetFileName(defaultSkaPath))} " +
                            "but it is an " +
                            "INTERMEDIATE authoring stream; skipping bind-pose enrichment[/]");
                    }
                    else if (defaultAnim.BoneTracks.Length == skeleton.Bones.Length)
                    {
                        skeleton = Ps2SkeletonDefaultPose.EnrichWithDefaultPose(skeleton, defaultAnim);
                        var relPath = Path.Combine(
                            Path.GetFileName(Path.GetDirectoryName(defaultSkaPath)) ?? string.Empty,
                            Path.GetFileName(defaultSkaPath));
                        AnsiConsole.MarkupLine(
                            $"Enriched V1 skeleton bind pose from [green]{Markup.Escape(relPath)}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]Found default anim {Markup.Escape(Path.GetFileName(defaultSkaPath))} " +
                            "but bone counts differ" +
                            $" ({defaultAnim.BoneTracks.Length} vs {skeleton.Bones.Length}); skipping enrichment[/]");
                    }
                }
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[yellow]V1 skeleton with no default animation found; bind pose will be identity (mesh may be distorted)[/]");
            }
        }

        var sw = Stopwatch.StartNew();
        var success = 0;
        var failed = 0;
        var totalBones = 0;
        var totalQKeys = 0;
        var totalTKeys = 0;
        var totalCustomKeys = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var data = File.ReadAllBytes(file);
                if (!SkaFile.IsSkaFile(data))
                {
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [grey]{Markup.Escape(Path.GetFileName(file))}: " +
                            "not a valid SKA file[/]");
                    }
                    failed++;
                    continue;
                }

                var compressTable = FindCompressTable(file);
                var anim = Thps4PcDatAnimationFile.IsCandidateFileName(file)
                    ? Thps4PcDatAnimationFile.ParseExact(data, compressTable)
                    : SkaFile.Parse(data, compressTable);
                success++;

                var boneCount = anim.BoneTracks.Length;
                var qCount = anim.BoneTracks.Sum(t => t.RotationKeys.Length);
                var tCount = anim.BoneTracks.Sum(t => t.TranslationKeys.Length);
                var customCount = anim.CustomKeys.Length;
                totalBones += boneCount;
                totalQKeys += qCount;
                totalTKeys += tCount;
                totalCustomKeys += customCount;

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  [green]{Markup.Escape(Path.GetFileName(file))}[/]: " +
                        $"v={anim.Version} bones={boneCount} " +
                        $"Q={qCount} T={tCount} custom={customCount} dur={anim.Duration:F2}s " +
                        $"flags=0x{anim.Flags:X8}");
                }

                var stem = GetOutputStem(file);

                if (customCount > 0)
                {
                    var customKeyPath = GetCustomKeyOutputPath(output, file, inputDirectoryRoot);
                    SkaCustomKeyJsonExporter.Write(customKeyPath, file, anim);
                    if (verbose)
                        AnsiConsole.MarkupLine(
                            $"    → [blue]{Markup.Escape(customKeyPath)}[/] " +
                            $"({customCount} custom events)");
                }

                if (anim.IsIntermediateFormat)
                {
                    // Authoring CUT masters carry an embedded checksum/name
                    // hierarchy but no proven neutral pose. Keep this path
                    // inspection-only even when --ske/--skin was supplied.
                    var inspectionPath = GetCustomKeyOutputPath(output, file, inputDirectoryRoot);
                    SkaIntermediateJsonExporter.Write(inspectionPath, file, anim);
                    if (verbose)
                        AnsiConsole.MarkupLine(
                            $"    → [blue]{Markup.Escape(inspectionPath)}[/] " +
                            "(intermediate authoring keys; no 3D export)");
                    continue;
                }

                if (qbKeyBoneMap != null && boneCount != qbKeyBoneMap.SourceBoneCount)
                {
                    throw new InvalidDataException(
                        $"SKA has {boneCount} tracks but --animation-ske has " +
                        $"{qbKeyBoneMap.SourceBoneCount} bones; exact QbKey binding was not applied.");
                }

                // THPS3 path: RW DFF .SKN as skeleton + mesh source.
                if (rwClump != null && sknPath != null)
                {
                    var result = ExportRwDffAnimated(
                        sknPath, stem, anim, output, texPath, format, blenderHelperPath,
                        cancellationToken);
                    if (verbose)
                    {
                        if (result.Triangles > 0)
                            AnsiConsole.MarkupLine(
                                $"    → [blue]{FormatOutputPaths(result)}[/] " +
                                $"(RW skinned, {result.Triangles} triangles)");
                        else
                            AnsiConsole.MarkupLine("    [yellow]skipped (bone count or clump not skinned)[/]");
                    }
                }
                else if (skeleton != null &&
                         (qbKeyBoneMap != null || boneCount == skeleton.Bones.Length) && skinPath != null &&
                         skinScene != null)
                {
                    // Combined mesh + animation via the unified pipeline.
                    var meshResult = ExportPs2SceneAnimated(
                        skinPath, stem, anim, skeleton, output, texPath, qbKeyBoneMap,
                        format, blenderHelperPath, cancellationToken);
                    if (verbose)
                        AnsiConsole.MarkupLine(
                            $"    → [blue]{FormatOutputPaths(meshResult)}[/] " +
                            $"(skinned, {meshResult.Triangles} triangles)");
                }
                else if (skeleton != null &&
                         (qbKeyBoneMap != null || boneCount == skeleton.Bones.Length))
                {
                    // Skeleton-only animation: build an IR document with no meshes.
                    var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
                        skeleton, [(stem, anim)], stem, qbKeyBoneMap);
                    var result = ExportDocument(
                        document, output, stem, format, blenderHelperPath, cancellationToken);
                    if (verbose)
                    {
                        var channelCount = document.Animations.Sum(a => a.Channels.Count);
                        AnsiConsole.MarkupLine(
                            $"    → [blue]{FormatOutputPaths(result)}[/] " +
                            $"({channelCount} channels)");
                    }
                }
                else if (skeleton == null && anim.IsThawFormat && anim.IsPlatformFormat && boneCount > 0)
                {
                    // THAW cutscene camera/object master (hi-res keys): no
                    // skeleton file exists for these, so synthesize a flat rig.
                    // Object tracks may carry QbKey names; camera tracks use a
                    // deterministic checksum/index fallback when they do not.
                    var rig = BuildThawObjectRig(anim);
                    var document = SkaModelDocumentBuilder.BuildSkeletonOnly(rig, [(stem, anim)], stem);
                    var result = ExportDocument(
                        document, output, stem, format, blenderHelperPath, cancellationToken);
                    if (verbose)
                    {
                        var channelCount = document.Animations.Sum(a => a.Channels.Count);
                        var kind = anim.IsCameraData ? "camera" : "object";
                        AnsiConsole.MarkupLine(
                            $"    → [blue]{FormatOutputPaths(result)}[/] " +
                            $"({kind} rig, {channelCount} channels)");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]{Markup.Escape(Path.GetFileName(file))}: " +
                        $"{Markup.Escape(ex.Message)}[/]");
                }
            }
        }

        sw.Stop();

        AnsiConsole.MarkupLine(
            $"\nParsed [green]{success}[/] animations " +
            $"([red]{failed}[/] failed) in {sw.Elapsed.TotalSeconds:F2}s");
        AnsiConsole.MarkupLine(
            $"Total: {totalQKeys:N0} rotation keys + {totalTKeys:N0} translation keys " +
            $"+ {totalCustomKeys:N0} custom events across {totalBones:N0} bone tracks");

        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    ///     Strip both the platform suffix and SKA extension when present, so
    ///     foo.ska, foo.ska.ps2 and foo.ska.ngc all share the output stem foo.
    /// </summary>
    internal static string GetOutputStem(string file)
    {
        var fileName = Path.GetFileName(file);
        if (Thps4PcDatAnimationFile.IsCandidateFileName(fileName))
            return fileName[..^Thps4PcDatAnimationFile.Suffix.Length];

        var suffix = SkaSuffixes.FirstOrDefault(candidate =>
            fileName.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
        return suffix == null
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName[..^suffix.Length];
    }

    internal static string GetCustomKeyOutputName(string file)
    {
        return GetOutputStem(file) + ".ska.json";
    }

    internal static bool IsUsableDefaultPose(SkaAnimation animation) =>
        !animation.IsIntermediateFormat;

    internal static Ps2Skeleton LoadSkeleton(string path)
    {
        return SkeletonAssetLoader.Parse(Path.GetFileName(path), File.ReadAllBytes(path));
    }

    /// <summary>
    ///     Keep directory-mode sidecars under their input-relative directory so
    ///     repeated names such as CAM_0.ska.ngc cannot overwrite one another.
    ///     Single-file mode intentionally retains the flat output name.
    /// </summary>
    internal static string GetCustomKeyOutputPath(
        string outputDirectory, string file, string? inputDirectoryRoot)
    {
        var outputRoot = Path.GetFullPath(outputDirectory);
        var relativeOutput = GetCustomKeyOutputName(file);

        if (inputDirectoryRoot != null)
        {
            var inputRoot = Path.GetFullPath(inputDirectoryRoot);
            var relativeInput = Path.GetRelativePath(inputRoot, Path.GetFullPath(file));
            if (Path.IsPathRooted(relativeInput) ||
                relativeInput.Equals("..", StringComparison.Ordinal) ||
                relativeInput.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativeInput.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"SKA sidecar input '{file}' is outside directory root '{inputDirectoryRoot}'");
            }

            relativeOutput = Path.Combine(
                Path.GetDirectoryName(relativeInput) ?? string.Empty,
                relativeOutput);
        }

        var outputPath = Path.GetFullPath(Path.Combine(outputRoot, relativeOutput));
        var outputRootPrefix = Path.EndsInDirectorySeparator(outputRoot)
            ? outputRoot
            : outputRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!outputPath.StartsWith(outputRootPrefix, pathComparison))
            throw new InvalidDataException(
                $"SKA sidecar path '{outputPath}' escapes output directory '{outputDirectory}'");

        return outputPath;
    }

    // THPS4 V1 skeletons (pre-2003) stored no bind pose in the .ske file; the engine
    // instead loaded a single-frame "default animation" per skeleton archetype
    // (e.g. pre/anims/anims/skater_basics/Default.ska.ps2 or the Windows
    // data/anims/skater_basics/defaultska.dat) and used
    // its frame-0 rotations+translations as the bind. THUG source deprecated this and
    // moved neutral poses into .ske V2 (see Gfx/Skeleton.cpp:1147-1152).
    //
    // Defaults are scattered across level-specific subfolders (pre/Alc/, pre/zoo/,
    // pre/cnv/, pre/hof/, pre/jnk/, pre/lon/, pre/sf2/, etc.) — the archetype
    // directory name matches the skeleton filename stem, with one exception: the
    // human 50-bone rig (skeletons/human.ske, Ped_F.ske, Ped_M.ske) maps to
    // archetype "skater_basics".
    //
    // Strategy: recursively search the paths' nearest shared directory. This
    // contains sibling model/animation trees without accidentally walking an
    // entire user profile or filesystem when a default is absent.
    internal static string? FindDefaultPoseFile(string skeletonPath, string anySkaFilePath)
    {
        var archetype = DeriveArchetypeName(skeletonPath);
        if (archetype == null) return null;

        var fullSkeletonPath = Path.GetFullPath(skeletonPath);
        var fullAnimationPath = Path.GetFullPath(anySkaFilePath);
        var commonDirectory = FindCommonDirectory(fullSkeletonPath, fullAnimationPath);
        var searchRoots = new List<string>();
        if (commonDirectory != null &&
            !commonDirectory.Equals(Path.GetPathRoot(commonDirectory), PathComparison))
        {
            searchRoots.Add(commonDirectory);
        }
        else
        {
            // Unrelated paths on the same volume share only its root. Keep that
            // degenerate case local to each input rather than recursively
            // enumerating the whole volume.
            AddContainingDirectory(fullSkeletonPath, searchRoots);
            AddContainingDirectory(fullAnimationPath, searchRoots);
        }

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                var directCandidate = FindDefaultPoseAtKnownLocation(root, archetype);
                if (directCandidate != null)
                    return directCandidate;

                foreach (var candidate in EnumerateDefaultPoseCandidates(root))
                {
                    var parentName = Path.GetFileName(Path.GetDirectoryName(candidate) ?? "");
                    if (string.Equals(parentName, archetype, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Skip directories we can't read.
            }
        }

        return null;
    }

    private static string? FindDefaultPoseAtKnownLocation(string root, string archetype)
    {
        ReadOnlySpan<string> animationDirectories =
        [
            "anims", "anims/anims", "Bits/anims", "bits/anims",
            "data/anims", "DATA/ANIMS", "pre/anims", "pre/anims/anims",
            "pre/Bits/anims", "pre/bits/anims", "Pre/Bits/anims"
        ];
        ReadOnlySpan<string> fileNames = ["defaultska.dat", "Default.ska.ps2", "default.ska.ps2"];

        foreach (var animationDirectory in animationDirectories)
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(root, animationDirectory, archetype, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDefaultPoseCandidates(string root)
    {
        // Walk each build tree once, then apply an exact case-insensitive name
        // gate to the small set of SKA-looking candidates.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };
        foreach (var candidate in Directory.EnumerateFiles(root, "*ska*", options))
        {
            var name = Path.GetFileName(candidate);
            if (name.Equals("default.ska.ps2", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("defaultska.dat", StringComparison.OrdinalIgnoreCase))
            {
                yield return candidate;
            }
        }
    }

    private static string? DeriveArchetypeName(string skeletonPath)
    {
        var stem = Path.GetFileNameWithoutExtension(skeletonPath);
        if (string.IsNullOrEmpty(stem)) return null;

        // The 50-bone human skeleton is shipped under several names (human, Ped_F, Ped_M, etc.)
        // all pointing to byte-identical .ske data. Its default anim lives under "skater_basics".
        if (stem.Equals("human", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("ped_", StringComparison.OrdinalIgnoreCase))
            return "skater_basics";

        return stem;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string? FindCommonDirectory(string firstPath, string secondPath)
    {
        var secondAncestors = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var directory = Path.GetDirectoryName(secondPath);
             !string.IsNullOrEmpty(directory);
             directory = Path.GetDirectoryName(directory))
        {
            secondAncestors.Add(directory);
        }

        for (var directory = Path.GetDirectoryName(firstPath);
             !string.IsNullOrEmpty(directory);
             directory = Path.GetDirectoryName(directory))
        {
            if (secondAncestors.Contains(directory))
                return directory;
        }

        return null;
    }

    private static void AddContainingDirectory(string path, List<string> results)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) &&
            !results.Contains(directory,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            results.Add(directory);
        }
    }

    private static MeshExportResult ExportRwDffAnimated(
        string sknPath,
        string stem,
        SkaAnimation animation,
        string outputDirectory,
        string? texPath,
        MeshOutputFormat format,
        string? blenderHelperPath,
        CancellationToken cancellationToken)
    {
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(sknPath),
            FileName = Path.GetFileName(sknPath),
            OutputStem = stem,
            SourceKind = ModelSourceKind.RenderWareDff,
            TexturePath = texPath,
            SkaAnimations = [(stem, animation)]
        });

        return ExportDocument(
            document, outputDirectory, stem, format, blenderHelperPath, cancellationToken);
    }

    private static MeshExportResult ExportPs2SceneAnimated(
        string skinPath,
        string stem,
        SkaAnimation animation,
        Ps2Skeleton skeleton,
        string outputDirectory,
        string? texPath,
        SkaQbKeyBoneMap? qbKeyBoneMap,
        MeshOutputFormat format,
        string? blenderHelperPath,
        CancellationToken cancellationToken)
    {
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(skinPath),
            FileName = Path.GetFileName(skinPath),
            OutputStem = stem,
            SourceKind = ModelSourceKind.Ps2Scene,
            TexturePath = texPath,
            PreparedSkeleton = skeleton,
            SkaAnimations = [(stem, animation)],
            SkaQbKeyBoneMap = qbKeyBoneMap
        });

        return ExportDocument(
            document, outputDirectory, stem, format, blenderHelperPath, cancellationToken);
    }

    internal static MeshExportResult ExportDocument(
        ModelDocument document,
        string outputDirectory,
        string stem,
        MeshOutputFormat format = MeshOutputFormat.Glb,
        string? blenderHelperPath = null,
        CancellationToken cancellationToken = default)
    {
        return ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = outputDirectory,
            OutputStem = stem,
            Format = format,
            BlenderHelperPath = blenderHelperPath,
            CancellationToken = cancellationToken
        });
    }

    private static string FormatOutputPaths(MeshExportResult result)
    {
        var paths = result.OutputPaths.Count == 0
            ? "no output"
            : string.Join(", ", result.OutputPaths.Select(Path.GetFileName));
        return Markup.Escape(paths);
    }

    /// <summary>
    ///     Flat identity rig for THAW cutscene camera/object master anims, whose
    ///     tracks target free-standing scene nodes rather than a skeleton. Node
    ///     names come from track QbKeys when bit24 supplies them (resolved via
    ///     the dbg dictionaries by the IR builder); unnamed camera tracks get a
    ///     deterministic index fallback.
    /// </summary>
    private static Ps2Skeleton BuildThawObjectRig(SkaAnimation anim)
    {
        var bones = new Ps2Bone[anim.BoneTracks.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            bones[i] = new Ps2Bone
            {
                NameChecksum = anim.BoneTracks[i].BoneNameChecksum ?? (uint)i,
                ParentChecksum = 0,
                FlipChecksum = 0,
                ParentIndex = -1,
                LocalRotation = Quaternion.Identity,
                LocalTranslation = Vector3.Zero,
                InverseBindMatrix = Matrix4x4.Identity
            };
        }

        return new Ps2Skeleton { Version = 2, Flags = 0, Bones = bones };
    }

    /// <summary>
    ///     Compression-table file names to try, in order. The PS3 builds suffix
    ///     the table file itself (<c>standardkeyQ.bin.ps3</c>), so a name-exact
    ///     search that only knows the bare form finds nothing and every
    ///     compressed clip fails with "requires a T48 compression table".
    /// </summary>
    private static readonly (string Q, string T)[] CompressTableNames =
    [
        ("standardkeyQ.bin", "standardkeyT.bin"),
        ("standardkeyq.bin", "standardkeyt.bin"),
        ("standardkeyQ.bin.ps3", "standardkeyT.bin.ps3"),
        ("standardkeyq.bin.ps3", "standardkeyt.bin.ps3"),
        ("standardkeyQ.bin.xen", "standardkeyT.bin.xen"),
        ("standardkeyq.bin.xen", "standardkeyt.bin.xen")
    ];

    internal static SkaCompressTable? FindCompressTable(string skaFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(skaFilePath));
        while (!string.IsNullOrEmpty(dir))
        {
            if (_tableCache.TryGetValue(dir, out var cached))
                return cached;

            // Standard locations (path conventions differ per game/platform):
            //   ../Bits/anims/standardkey{Q,T}.bin  (THPS4 PS2: pre/Bits/anims/, THUG PS2: Pre/Bits/anims/)
            //   ../anims/standardkey{Q,T}.bin       (THUG2/THAW/P8/PG PS2: DATAP/anims/, THAW GC)
            //   ../bits/anims/standardkey{Q,T}.bin  (THUG2 nested: DATAP/pre/bits/anims/)
            //   ../pre/Bits/anims/standardkey{Q,T}.bin (THUG2 Xbox: data/pre/Bits/anims/, tables
            //                                          under pre/ while cutscenes sit in data/)
            //   ../BIN/standardkey{Q,T}.bin         (reserved for future builds if any use it)
            //   ../data/anims/standardkey{Q,T}.bin      (THAW/P8/PG Xbox 360: data/anims/)
            //   ../DATA/ANIMS/standardkey{Q,T}.bin.ps3  (THAW/P8/PG PS3, which also
            //                                            suffixes the table FILENAME)
            foreach (var subdir in new[]
                     {
                         "BIN", "bin", "anims", "Bits/anims", "bits/anims", "pre/anims", "pre/Bits/anims",
                         "pre/bits/anims", "data/anims", "DATA/ANIMS"
                     })
            {
                foreach (var (qName, tName) in CompressTableNames)
                {
                    var qPath = Path.Combine(dir, subdir, qName);
                    var tPath = Path.Combine(dir, subdir, tName);
                    if (!File.Exists(qPath) || !File.Exists(tPath))
                        continue;

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
