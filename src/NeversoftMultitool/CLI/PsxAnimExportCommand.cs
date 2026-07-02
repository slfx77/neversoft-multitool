using System.CommandLine;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Single-file CLI: parse a PSX character, decode its embedded animations,
///     and emit an animated <c>.glb</c> through the unified
///     <see cref="ModelExportService" /> pipeline.
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
        var animSourceOption = new Option<string?>("--anim-source")
        {
            Description =
                "Optional external PSX animation bank to merge with the input character's embedded animations"
        };
        var fpsOption = new Option<float>("--fps")
        {
            Description =
                "Frame rate for time-base conversion (default: 10). The engine runs at 30fps " +
                "natively, but PSX character files often ship animations with only a handful " +
                "of frames (mullen.psx has 2; carnage entries vary 17-56) - at 30fps these loop " +
                "in a fraction of a second and look like flicker. 10fps gives a readable preview.",
            DefaultValueFactory = _ => 10f
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "Override the default anim_N name (only valid with --anim N)"
        };
        var noRotOption = new Option<bool>("--no-rot")
        {
            Description = "Diagnostic: skip rotation tracks (bones keep bind rotation)"
        };
        var withTransOption = new Option<bool>("--with-trans")
        {
            Description =
                "Diagnostic: emit bind-anchored per-bone translation tracks using the runtime " +
                "Super SMatrix /16 translation shift."
        };
        var transBonesOption = new Option<string?>("--trans-bones")
        {
            Description =
                "Diagnostic: with --with-trans, only emit translation tracks for this comma/range " +
                "bone list (for example 16 or 16-18)."
        };
        var transDivisorScaleOption = new Option<float>("--trans-divisor-scale")
        {
            Description =
                "Diagnostic: with --with-trans, multiply the animation translation divisor. " +
                "Default 16 matches the runtime's Super SMatrix right-shift; use 1 to inspect " +
                "unshifted raw translations.",
            DefaultValueFactory = _ => 16f
        };
        var transAbsoluteOption = new Option<bool>("--trans-absolute")
        {
            Description =
                "Diagnostic: with --with-trans, emit raw PSX Tx/Ty/Tz as absolute node translation " +
                "instead of frame-0-anchored bind deltas."
        };
        var transEngineWorldOption = new Option<bool>("--trans-engine-world")
        {
            Description =
                "Diagnostic: with --with-trans, recursively compose PSX Tx/Ty/Tz like " +
                "Decomp_GetAnimTransform and solve the world-space result back to glTF locals. " +
                "This is opt-in because it can worsen the current visual pose."
        };
        var transSourceHierarchyOption = new Option<bool>("--trans-source-hierarchy")
        {
            Description =
                "Diagnostic: with --with-trans --trans-engine-world, compose translation recursion " +
                "with the animation bank's parsed hierarchy, remapped to the target bone order."
        };
        var rotComposeOption = new Option<string>("--rot-compose")
        {
            Description = "Quaternion composition order: yxz (default), zxy, xyz, zyx, xzy, yzx",
            DefaultValueFactory = _ => "yxz"
        };
        var rotScaleOption = new Option<float>("--rot-scale")
        {
            Description =
                "Diagnostic: multiply decoded rotation angles before export (default: 1.0). " +
                "Use values below 1.0 to test suspected rotation over-amplification.",
            DefaultValueFactory = _ => 1f
        };
        var legacyChainOption = new Option<bool>("--legacy-rot-chain")
        {
            Description =
                "Diagnostic: emit raw local rotations and let glTF chain them (pre-piecewise-rigid behaviour). " +
                "Use to A/B compare against the default piecewise-rigid composition that mirrors " +
                "the THPS2 engine's Decomp_GetAnimTransform."
        };
        var flatSkeletonOption = new Option<bool>("--psx-flat-skeleton")
        {
            Description =
                "Diagnostic: emit PSX character joints as flat world-space body-part matrices instead " +
                "of a parented glTF skeleton. This better matches the engine's per-part SMatrix renderer."
        };
        var flatBonesOption = new Option<string?>("--psx-flat-bones")
        {
            Description =
                "Diagnostic: emit only this comma/range bone list as flat root-side PSX body parts " +
                "while leaving the rest of the skeleton parented."
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("psx-anim-export",
            "Export a PS1 character .psx as an animated .glb (one or all embedded animations)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(animOption);
        command.Options.Add(animSourceOption);
        command.Options.Add(fpsOption);
        command.Options.Add(nameOption);
        command.Options.Add(noRotOption);
        command.Options.Add(withTransOption);
        command.Options.Add(transBonesOption);
        command.Options.Add(transDivisorScaleOption);
        command.Options.Add(transAbsoluteOption);
        command.Options.Add(transEngineWorldOption);
        command.Options.Add(transSourceHierarchyOption);
        command.Options.Add(rotComposeOption);
        command.Options.Add(rotScaleOption);
        command.Options.Add(legacyChainOption);
        command.Options.Add(flatSkeletonOption);
        command.Options.Add(flatBonesOption);
        command.Options.Add(formatOption);
        command.Options.Add(blenderHelperOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var anim = parseResult.GetValue(animOption);
            var animSource = parseResult.GetValue(animSourceOption);
            var fps = parseResult.GetValue(fpsOption);
            var name = parseResult.GetValue(nameOption);
            var noRot = parseResult.GetValue(noRotOption);
            var withTrans = parseResult.GetValue(withTransOption);
            var transBones = parseResult.GetValue(transBonesOption);
            var transDivisorScale = parseResult.GetValue(transDivisorScaleOption);
            var transAbsolute = parseResult.GetValue(transAbsoluteOption);
            var transEngineWorld = parseResult.GetValue(transEngineWorldOption);
            var transSourceHierarchy = parseResult.GetValue(transSourceHierarchyOption);
            var rotCompose = parseResult.GetValue(rotComposeOption);
            var rotScale = parseResult.GetValue(rotScaleOption);
            var legacyChain = parseResult.GetValue(legacyChainOption);
            var flatSkeleton = parseResult.GetValue(flatSkeletonOption);
            var flatBones = parseResult.GetValue(flatBonesOption);
            var formatValue = parseResult.GetValue(formatOption);
            var blenderHelper = parseResult.GetValue(blenderHelperOption);
            var verbose = parseResult.GetValue(verboseOption);
            if (!MeshExportCliOptions.ValidateFormat(formatValue, out var format))
                return Task.FromResult(1);
            if (!TryParseBoneList(transBones, "--trans-bones", out var translationBoneFilter))
                return Task.FromResult(1);
            if (!TryParseBoneList(flatBones, "--psx-flat-bones", out var flatBoneFilter))
                return Task.FromResult(1);
            var opts = new PsxAnimationOptions(
                SkipRotation: noRot,
                SkipTranslation: !withTrans,
                RotationCompose: ParseRotCompose(rotCompose ?? "yxz"),
                Fps: fps,
                LegacyRotationChain: legacyChain,
                RotationScale: SanitizeRotationScale(rotScale),
                TranslationBoneFilter: translationBoneFilter,
                TranslationDivisorScale: SanitizePositiveScale(transDivisorScale, "--trans-divisor-scale"),
                AbsoluteTranslation: transAbsolute,
                EngineWorldTranslation: transEngineWorld,
                SourceHierarchyTranslation: transSourceHierarchy);
            return Task.FromResult(Execute(
                input, output, animSource, anim, name, opts, format, blenderHelper,
                flatSkeleton, flatBoneFilter, verbose));
        });

        return command;
    }

    private static PsxRotationCompose ParseRotCompose(string s)
    {
        if (Enum.TryParse<PsxRotationCompose>(s, true, out var compose))
            return compose;
        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] Unknown --rot-compose value '{Markup.Escape(s)}'; using YXZ.");
        return PsxRotationCompose.YXZ;
    }

    private static float SanitizeRotationScale(float value)
    {
        if (float.IsFinite(value) && value >= 0f)
            return value;

        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] Invalid --rot-scale value '{value}'; using 1.0.");
        return 1f;
    }

    private static float SanitizePositiveScale(float value, string optionName)
    {
        if (float.IsFinite(value) && value > 0f)
            return value;

        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] Invalid {optionName} value '{value}'; using 1.0.");
        return 1f;
    }

    private static bool TryParseBoneList(
        string? value,
        string optionName,
        out IReadOnlySet<int>? boneFilter)
    {
        boneFilter = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var parsed = new HashSet<int>();
        foreach (var rawPart in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = rawPart.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                if (!int.TryParse(rawPart[..dash], out var start)
                    || !int.TryParse(rawPart[(dash + 1)..], out var end)
                    || start < 0
                    || end < start)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Error:[/] Invalid {optionName} range: {Markup.Escape(rawPart)}");
                    return false;
                }

                for (var bone = start; bone <= end; bone++)
                    parsed.Add(bone);
                continue;
            }

            if (!int.TryParse(rawPart, out var index) || index < 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Invalid {optionName} index: {Markup.Escape(rawPart)}");
                return false;
            }

            parsed.Add(index);
        }

        if (parsed.Count == 0)
            return true;

        boneFilter = parsed;
        return true;
    }

    private static string FormatSkeletonMode(bool flatSkeleton, IReadOnlySet<int>? flatBoneFilter)
    {
        if (flatSkeleton)
            return "flat";
        if (flatBoneFilter is { Count: > 0 } filter)
            return $"partial-flat({string.Join(",", filter.Order())})";
        return "hier";
    }

    private static int Execute(
        string input, string? output, string? animSourcePath, int animIndex, string? animName,
        PsxAnimationOptions opts, MeshOutputFormat format, string? blenderHelper,
        bool flatSkeleton, IReadOnlySet<int>? flatBoneFilter, bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return 1;
        }

        var inputSource = new FileSystemAssetSource(input);
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
                "[yellow]Warning:[/] PSX file is not a hierarchical character - " +
                "animations are only valid for character models.");
        }

        FileSystemAssetSource? externalSource = null;
        if (!string.IsNullOrWhiteSpace(animSourcePath))
        {
            if (!File.Exists(animSourcePath))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Animation source not found: {animSourcePath}");
                return 1;
            }

            externalSource = new FileSystemAssetSource(animSourcePath);
        }

        var targetBoneCount = psxFile.Objects.Count;
        var embeddedBank = PsxAnimationBank.TryProbe(inputSource, data, targetBoneCount);
        if ((embeddedBank == null || embeddedBank.AnimFile.Entries.Count == 0) && externalSource == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No recognizable animation table in this PSX file.");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(animName) && externalSource != null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] --name is ignored when --anim-source is used; " +
                "bank prefixes are required to avoid duplicate animation names.");
        }

        var banks =
            new List<(string Kind, string? Prefix, PsxAnimationBankInfo Bank, PsxAnimationBoneRemap? Remap,
                int[]? TranslationParents)>();
        if (embeddedBank != null)
        {
            var prefix = externalSource == null
                ? null
                : Path.GetFileNameWithoutExtension(input);
            banks.Add(("input", prefix, embeddedBank, null, null));
        }

        if (externalSource != null)
        {
            var externalBank = PsxAnimationBank.TryProbe(externalSource, targetBoneCount);
            if (externalBank == null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] No recognizable animation table in animation source: {animSourcePath}");
                return 1;
            }

            if (!externalBank.MatchesTargetBoneCount)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Animation source has {externalBank.BoneCount} bones; " +
                    $"character has {targetBoneCount}: {animSourcePath}");
                return 1;
            }

            var remap = PsxAnimationBoneMap.TryCreate(
                externalSource, inputSource, targetBoneCount, out var remapDiagnostic);
            if (remap is { IsIdentity: false })
            {
                AnsiConsole.MarkupLine(
                    $"[grey]external bone remap:[/] {remap.RemappedCount} " +
                    "source PSH slot(s) reordered for target character.");
            }
            else if (verbose && remap == null && remapDiagnostic != null)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]external bone remap:[/] not applied ({Markup.Escape(remapDiagnostic)}).");
            }

            var translationParents = opts.SourceHierarchyTranslation
                ? BuildRemappedAnimationParentIndices(externalBank, targetBoneCount, remap, verbose)
                : null;

            banks.Add((
                "external",
                Path.GetFileNameWithoutExtension(externalSource.EntryName),
                externalBank,
                remap,
                translationParents));
        }

        var decoded = new List<PsxAnimationClip>();
        foreach (var (kind, prefix, bank, remap, translationParents) in banks)
        {
            PrintBankSummary(kind, bank);

            var selected = PsxAnimationBank.ResolveSelections(
                bank.AnimFile,
                animIndex,
                externalSource == null ? animName : null,
                prefix);
            if (selected.Count == 0)
                continue;

            var decodeResult = PsxAnimationBank.Decode(bank, targetBoneCount, selected, remap);
            decoded.AddRange(decodeResult.Animations.Select(entry =>
                new PsxAnimationClip(entry.Name, entry.Animation, translationParents)));
            PrintDecodeDiagnostics(decodeResult.Diagnostics, verbose);
        }

        if (decoded.Count == 0)
        {
            AnsiConsole.MarkupLine(
                animIndex >= 0
                    ? $"[red]Error:[/] Animation index {animIndex} out of range for all active banks."
                    : "[red]Error:[/] No animations decoded successfully.");
            return 1;
        }

        var outputPath = output ?? DeriveOutputPath(input);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDir))
            outputDir = ".";
        var outputStem = Path.GetFileNameWithoutExtension(outputPath);

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = inputSource,
            FileName = Path.GetFileName(input),
            OutputStem = outputStem,
            SourceKind = ModelSourceKind.Psx,
            PsxAnimationOptions = opts,
            PsxAnimationClips = decoded,
            PsxFlatSkeleton = flatSkeleton,
            PsxFlatBoneIndices = flatBoneFilter
        });

        var result = ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = outputDir,
            OutputStem = outputStem,
            Format = format,
            BlenderHelperPath = blenderHelper
        });

        var emittedPaths = result.OutputPaths.Count > 0
            ? string.Join(", ", result.OutputPaths.Select(Path.GetFileName))
            : Path.GetFileName(outputPath);
        string transStatus;
        if (opts.SkipTranslation)
        {
            transStatus = "off";
        }
        else if (opts.TranslationBoneFilter is { Count: > 0 } filter)
        {
            transStatus = $"filtered({string.Join(",", filter.Order())})";
        }
        else
        {
            transStatus = "on";
        }

        string transMode;
        if (opts.EngineWorldTranslation)
        {
            transMode = opts.AbsoluteTranslation ? "engine-world-absolute" : "engine-world-delta";
            if (opts.SourceHierarchyTranslation)
                transMode += "+source-hier";
        }
        else
        {
            transMode = opts.AbsoluteTranslation ? "absolute" : "delta";
        }

        AnsiConsole.MarkupLine(
            $"[green]Wrote[/] {Markup.Escape(emittedPaths)}  " +
            $"triangles={result.Triangles:N0}  animations={decoded.Count}  fps={opts.Fps:F1}  " +
            $"compose={opts.RotationCompose}  rot={(opts.SkipRotation ? "off" : "on")}  " +
            $"trans={transStatus}  transDivScale={opts.TranslationDivisorScale:F3}  " +
            $"transMode={transMode}  " +
            $"skeleton={FormatSkeletonMode(flatSkeleton, flatBoneFilter)}  " +
            $"rotScale={opts.RotationScale:F3}");

        return result.Triangles == 0 ? 1 : 0;
    }

    private static int[]? BuildRemappedAnimationParentIndices(
        PsxAnimationBankInfo bank,
        int targetBoneCount,
        PsxAnimationBoneRemap? remap,
        bool verbose)
    {
        PsxMeshFile? sourceHeader;
        try
        {
            sourceHeader = PsxMeshFile.ParseHeaderOnly(bank.Source.ReadBytes());
        }
        catch
        {
            sourceHeader = null;
        }

        if (sourceHeader == null || sourceHeader.Objects.Count == 0)
        {
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]source hierarchy:[/] unavailable for {Markup.Escape(bank.Source.DisplayName)}.");
            }

            return null;
        }

        var sourceLimit = Math.Min(targetBoneCount, sourceHeader.Objects.Count);
        var sourceToTarget = new int[sourceLimit];
        for (var source = 0; source < sourceToTarget.Length; source++)
        {
            sourceToTarget[source] = remap != null && source < remap.SourceToTarget.Count
                ? remap.SourceToTarget[source]
                : source;
        }

        var targetParents = new int[targetBoneCount];
        Array.Fill(targetParents, -1);
        var changed = 0;
        for (var source = 0; source < sourceLimit; source++)
        {
            var target = sourceToTarget[source];
            if (target < 0 || target >= targetBoneCount)
                continue;

            var sourceParent = sourceHeader.Objects[source].ParentIndex;
            var targetParent = -1;
            if (sourceParent >= 0 && sourceParent < sourceToTarget.Length)
                targetParent = sourceToTarget[sourceParent];

            if (targetParent < 0 || targetParent >= targetBoneCount || targetParent == target)
                targetParent = -1;

            targetParents[target] = targetParent;
        }

        for (var target = 0; target < targetParents.Length; target++)
        {
            var modelParent = target < sourceHeader.Objects.Count
                ? sourceHeader.Objects[target].ParentIndex
                : -1;
            if (targetParents[target] != modelParent)
                changed++;
        }

        if (verbose)
        {
            AnsiConsole.MarkupLine(
                $"[grey]source hierarchy:[/] remapped {sourceLimit} source parent entries; " +
                $"{changed} target slot(s) differ from same-index source parents.");
        }

        return targetParents;
    }

    private static void PrintBankSummary(string kind, PsxAnimationBankInfo bank)
    {
        AnsiConsole.MarkupLine(
            $"[bold]{Markup.Escape(kind)} layout:[/] {bank.AnimFile.Layout}  " +
            $"revision={bank.AnimFile.FormatRevision}  " +
            $"runtime={bank.AnimFile.MinimumRuntimeRevision}  " +
            $"numStreams={bank.AnimFile.NumStreamsDeclared}  " +
            $"recoverable={bank.AnimFile.Entries.Count}  bones={bank.BoneCount}");

        // Only the THPS2-prototype-style sparse table fails to expose all
        // entries; v1 DirectMatrix and v2 Monolithic both decode every slot.
        if (bank.AnimFile.Layout == PsxAnimLayoutVariant.PrototypeSparse)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Note:[/] sparse entry table - only the first animation is recoverable.");
        }

        if (bank.AnimFile.Layout == PsxAnimLayoutVariant.DirectMatrix)
        {
            AnsiConsole.MarkupLine(
                "[grey]Note:[/] v1 (0x2A) char files store rotation matrices directly. " +
                "Some prototype/test files (e.g. hawk2.psx) ship rest poses rotated ~180 degrees from " +
                "the obj.Position bind - pass [bold]--no-rot[/] to preview the bind pose only.");
        }
    }

    private static void PrintDecodeDiagnostics(
        IReadOnlyList<PsxAnimationDecodeDiagnostic> diagnostics,
        bool verbose)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Succeeded)
            {
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  [grey]{diagnostic.Name,-20}[/] frames={diagnostic.FrameCount,4}  " +
                        $"bytesConsumed={diagnostic.BytesConsumed,5}");
                }

                continue;
            }

            AnsiConsole.MarkupLine(
                $"  [yellow]{Markup.Escape(diagnostic.Name)}: decode failed " +
                $"({Markup.Escape(diagnostic.Error ?? "unknown error")})[/]");
        }
    }

    private static string DeriveOutputPath(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(dir, stem + "_animated.glb");
    }
}
