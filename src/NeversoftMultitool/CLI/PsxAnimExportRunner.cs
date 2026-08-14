using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     The psx-anim-export pipeline: parse the character, probe/decode the
///     embedded and external animation banks, and export the animated GLB.
/// </summary>
internal static class PsxAnimExportRunner
{
    private static string FormatSkeletonMode(bool flatSkeleton, IReadOnlySet<int>? flatBoneFilter)
    {
        if (flatSkeleton)
            return "flat";
        if (flatBoneFilter is { Count: > 0 } filter)
            return $"partial-flat({string.Join(",", filter.Order())})";
        return "hier";
    }

    internal static int Run(
        string input, string? output, string? animSourcePath, int animIndex, string? animName,
        PsxAnimationOptions opts, MeshOutputFormat format, string? blenderHelper,
        bool flatSkeleton, IReadOnlySet<int>? flatBoneFilter, bool verbose,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        var inputSource = new FileSystemAssetSource(input);
        var data = File.ReadAllBytes(input);
        cancellationToken.ThrowIfCancellationRequested();
        var psxFile = PsxMeshFile.Parse(data);
        cancellationToken.ThrowIfCancellationRequested();
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
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Animation source not found: {Markup.Escape(animSourcePath)}");
                return 1;
            }

            externalSource = new FileSystemAssetSource(animSourcePath);
        }

        var targetBoneCount = psxFile.Objects.Count;
        var embeddedBank = PsxAnimationBank.TryProbe(inputSource, data, targetBoneCount);
        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            if (externalBank == null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] No recognizable animation table in animation source: " +
                    Markup.Escape(animSourcePath!));
                return 1;
            }

            if (!externalBank.MatchesTargetBoneCount)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Animation source has {externalBank.BoneCount} bones; " +
                    $"character has {targetBoneCount}: {Markup.Escape(animSourcePath!)}");
                return 1;
            }

            var remap = PsxAnimationBoneMap.TryCreate(
                externalSource, inputSource, targetBoneCount, out var remapDiagnostic);
            cancellationToken.ThrowIfCancellationRequested();
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

            var translationParents = PsxAnimationBank.TryBuildSourceParentIndices(
                externalBank.Source, targetBoneCount, remap);
            cancellationToken.ThrowIfCancellationRequested();
            if (verbose)
            {
                AnsiConsole.MarkupLine(translationParents != null
                    ? "[grey]source hierarchy:[/] bank parent table attached for translation composition."
                    : $"[grey]source hierarchy:[/] unavailable for {Markup.Escape(externalBank.Source.DisplayName)}.");
            }

            banks.Add((
                "external",
                Path.GetFileNameWithoutExtension(externalSource.EntryName),
                externalBank,
                remap,
                translationParents));
        }

        // PSX slots are unnamed (anim_N); prefix them with the mesh stem so the
        // exported clips read as docock_anim_N, matching the GUI export path
        // (CharacterAnimationConverter.BuildPsx). Authored/--name'd and external
        // bank-prefixed names are left untouched by ForMesh.
        var meshStem = Path.GetFileNameWithoutExtension(input);
        var usedAnimNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var decoded = new List<PsxAnimationClip>();
        foreach (var (kind, prefix, bank, remap, translationParents) in banks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrintBankSummary(kind, bank);

            var selected = PsxAnimationBank.ResolveSelections(
                bank.AnimFile,
                animIndex,
                externalSource == null ? animName : null,
                prefix);
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Count == 0)
                continue;

            var decodeResult = PsxAnimationBank.Decode(
                bank, targetBoneCount, selected, remap, opts.OneShot);
            cancellationToken.ThrowIfCancellationRequested();
            decoded.AddRange(decodeResult.Animations.Select(entry =>
                new PsxAnimationClip(
                    AnimationExportName.ForMesh(meshStem, entry.Name, usedAnimNames),
                    entry.Animation,
                    translationParents)));
            PrintDecodeDiagnostics(decodeResult.Diagnostics, verbose);
        }

        cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();

        var result = ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = outputDir,
            OutputStem = outputStem,
            Format = format,
            BlenderHelperPath = blenderHelper,
            CancellationToken = cancellationToken
        });
        cancellationToken.ThrowIfCancellationRequested();

        if (result.OutputPaths.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Animation export produced no output.");
            return 1;
        }

        var emittedPaths = string.Join(", ", result.OutputPaths.Select(Path.GetFileName));
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
        }
        else
        {
            // The adapter auto-routes to the engine-world solve when a clip's
            // bank hierarchy differs from the character's, so "absolute" here
            // means "contract default", not "always local".
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

        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    private static void PrintBankSummary(string kind, PsxAnimationBankInfo bank)
    {
        AnsiConsole.MarkupLine(
            $"[bold]{Markup.Escape(kind)} layout:[/] {bank.AnimFile.Layout}  " +
            $"revision={bank.AnimFile.FormatRevision}  " +
            $"runtime={bank.AnimFile.MinimumRuntimeRevision}  " +
            $"numStreams={bank.AnimFile.NumStreamsDeclared}  " +
            $"recoverable={bank.AnimFile.Entries.Count}  bones={bank.BoneCount}");

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
                        $"  [grey]{Markup.Escape(diagnostic.Name.PadRight(20))}[/] " +
                        $"frames={diagnostic.FrameCount,4}  " +
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
