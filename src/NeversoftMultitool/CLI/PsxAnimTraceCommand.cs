using System.CommandLine;
using System.Globalization;
using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Rendering;
using SharpGLTF.Schema2;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Numeric PSX animation diagnostic. Compares decoded Tx/Ty/Tz channels,
///     the current glTF exporter target, the engine-recursive
///     <c>Decomp_GetAnimTransform</c> target, and an optional sampled GLB.
/// </summary>
public static class PsxAnimTraceCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to the target PSX character file"
        };
        var animSourceOption = new Option<string?>("--anim-source")
        {
            Description = "Optional external PSX animation bank"
        };
        var animOption = new Option<int>("--anim")
        {
            Description = "Animation slot to decode",
            DefaultValueFactory = _ => 0
        };
        var frameOption = new Option<int>("--frame")
        {
            Description = "Frame to trace",
            DefaultValueFactory = _ => 0
        };
        var fpsOption = new Option<float>("--fps")
        {
            Description = "Frame rate used to map frame to GLB time",
            DefaultValueFactory = _ => PsxAnimationBank.DefaultPreviewFps
        };
        var bonesOption = new Option<string?>("--bones")
        {
            Description = "Comma/range bone list to print (default: 0,1,4,16-18)"
        };
        var glbOption = new Option<string?>("--glb")
        {
            Description = "Optional exported GLB to sample at the same frame time"
        };
        var glbAnimOption = new Option<int>("--glb-anim")
        {
            Description = "GLB animation index to sample",
            DefaultValueFactory = _ => 0
        };
        var transDivisorScaleOption = new Option<float>("--trans-divisor-scale")
        {
            Description =
                "Exporter translation divisor multiplier to trace. Defaults to the current PSX animation option.",
            DefaultValueFactory = _ => new PsxAnimationOptions().TranslationDivisorScale
        };
        var rotComposeOption = new Option<string>("--rot-compose")
        {
            Description = "Quaternion composition order: yxz (default), zxy, xyz, zyx, xzy, yzx",
            DefaultValueFactory = _ => "yxz"
        };
        var rotScaleOption = new Option<float>("--rot-scale")
        {
            Description = "Diagnostic multiplier applied to decoded rotation angles",
            DefaultValueFactory = _ => 1f
        };
        var flatSkeletonOption = new Option<bool>("--psx-flat-skeleton")
        {
            Description =
                "Trace against the diagnostic flat PSX skeleton representation used by psx-anim-export."
        };
        var flatBonesOption = new Option<string?>("--psx-flat-bones")
        {
            Description =
                "Trace against a partial-flat PSX skeleton using this comma/range bone list."
        };
        var vertexBoundsOption = new Option<bool>("--vertex-bounds")
        {
            Description =
                "Print per-bone mesh vertex bounds for GLB skinning and PSX-style body-part transforms."
        };

        var command = new Command("psx-anim-trace",
            "Trace PSX animation bone transforms against exporter and optional GLB output");
        command.Arguments.Add(inputArgument);
        command.Options.Add(animSourceOption);
        command.Options.Add(animOption);
        command.Options.Add(frameOption);
        command.Options.Add(fpsOption);
        command.Options.Add(bonesOption);
        command.Options.Add(glbOption);
        command.Options.Add(glbAnimOption);
        command.Options.Add(transDivisorScaleOption);
        command.Options.Add(rotComposeOption);
        command.Options.Add(rotScaleOption);
        command.Options.Add(flatSkeletonOption);
        command.Options.Add(flatBonesOption);
        command.Options.Add(vertexBoundsOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            var input = parseResult.GetValue(inputArgument)!;
            var animSource = parseResult.GetValue(animSourceOption);
            var anim = parseResult.GetValue(animOption);
            var frame = parseResult.GetValue(frameOption);
            var fps = parseResult.GetValue(fpsOption);
            var bones = parseResult.GetValue(bonesOption);
            var glb = parseResult.GetValue(glbOption);
            var glbAnim = parseResult.GetValue(glbAnimOption);
            var transDivisorScale = parseResult.GetValue(transDivisorScaleOption);
            var rotCompose = parseResult.GetValue(rotComposeOption);
            var rotScale = parseResult.GetValue(rotScaleOption);
            var flatSkeleton = parseResult.GetValue(flatSkeletonOption);
            var flatBones = parseResult.GetValue(flatBonesOption);
            var vertexBounds = parseResult.GetValue(vertexBoundsOption);
            return Task.FromResult(Execute(
                input, animSource, anim, frame, fps, bones, glb, glbAnim,
                transDivisorScale, rotCompose, rotScale, flatSkeleton, flatBones,
                vertexBounds));
        });

        return command;
    }

    private static int Execute(
        string input,
        string? animSourcePath,
        int animIndex,
        int frame,
        float fps,
        string? bonesSpec,
        string? glbPath,
        int glbAnimIndex,
        float transDivisorScale,
        string? rotComposeText,
        float rotScale,
        bool flatSkeleton,
        string? flatBonesSpec,
        bool vertexBounds)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        var inputSource = new FileSystemAssetSource(input);
        var psxData = File.ReadAllBytes(input);
        var psxFile = PsxMeshFile.Parse(psxData);
        if (psxFile == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] PSX file has no parseable mesh data.");
            return 1;
        }

        var targetBoneCount = psxFile.Objects.Count;
        if (targetBoneCount == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] PSX file has no object/bone table.");
            return 1;
        }

        var bankSource = inputSource;
        var bankKind = "input";
        if (!string.IsNullOrWhiteSpace(animSourcePath))
        {
            if (!File.Exists(animSourcePath))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] Animation source not found: {Markup.Escape(animSourcePath)}");
                return 1;
            }

            bankSource = new FileSystemAssetSource(animSourcePath);
            bankKind = "external";
        }

        var bank = PsxAnimationBank.TryProbe(bankSource, targetBoneCount);
        if (bank == null)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] No recognizable PSX animation table in {Markup.Escape(bankSource.DisplayName)}.");
            return 1;
        }

        if (!bank.MatchesTargetBoneCount)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Animation bank has {bank.BoneCount} bones; character has {targetBoneCount}.");
            return 1;
        }

        string? remapDiagnostic = null;
        var remap = IsSameAsset(bankSource, inputSource)
            ? null
            : PsxAnimationBoneMap.TryCreate(
                bankSource, inputSource, targetBoneCount, out remapDiagnostic);
        var selected = PsxAnimationBank.ResolveSelections(
            bank.AnimFile, animIndex, animName: null, namePrefix: null);
        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Animation index {animIndex} out of range (0..{bank.AnimFile.Entries.Count - 1}).");
            return 1;
        }

        var decoded = PsxAnimationBank.Decode(bank, targetBoneCount, selected, remap);
        if (decoded.Animations.Count != 1)
        {
            var error = decoded.Diagnostics.FirstOrDefault()?.Error ?? "decode failed";
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not decode animation: {Markup.Escape(error)}");
            return 1;
        }

        var animation = decoded.Animations[0].Animation;
        var sourceDecoded = PsxAnimationBank.Decode(bank, bank.BoneCount, selected);
        var sourceAnimation = sourceDecoded.Animations.Count == 1
            ? sourceDecoded.Animations[0].Animation
            : animation;
        var sourcePsxFile = IsSameAsset(bankSource, inputSource)
            ? psxFile
            : PsxMeshFile.ParseHeaderOnly(bankSource.ReadBytes()) ?? psxFile;
        var renderAnimOrder = BuildRenderAnimOrder(
            bankSource, inputSource, targetBoneCount, remap, remapDiagnostic);
        if (animation.FrameCount == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Animation has no frames.");
            return 1;
        }

        frame = Math.Clamp(frame, 0, animation.FrameCount - 1);
        fps = fps > 0f && float.IsFinite(fps) ? fps : PsxAnimationBank.DefaultPreviewFps;
        var time = frame / fps;
        var compose = ParseRotCompose(rotComposeText ?? "yxz");
        rotScale = float.IsFinite(rotScale) && rotScale >= 0f ? rotScale : 1f;
        var flatBoneFilter = ResolveOptionalBones(flatBonesSpec, targetBoneCount);

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = inputSource,
            FileName = Path.GetFileName(input),
            OutputStem = Path.GetFileNameWithoutExtension(input),
            SourceKind = ModelSourceKind.Psx,
            PsxFlatSkeleton = flatSkeleton,
            PsxFlatBoneIndices = flatBoneFilter
        });
        if (document.Skeletons.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Parsed character has no skeleton.");
            return 1;
        }

        var skeleton = document.Skeletons[0];
        var boneCount = Math.Min(skeleton.Bones.Count, animation.BoneCount);
        var bones = ResolveBones(bonesSpec, boneCount);
        if (bones.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Bone filter selected no valid bones.");
            return 1;
        }

        var baseTranslationDivisor = psxFile.ScaleDivisor > 0f
            ? psxFile.ScaleDivisor
            : psxFile.TranslationDivisor;
        transDivisorScale = float.IsFinite(transDivisorScale) && transDivisorScale > 0f
            ? transDivisorScale
            : 1f;
        var exporterDivisor = baseTranslationDivisor * transDivisorScale;

        var gltfParentIndices = new int[boneCount];
        for (var i = 0; i < boneCount; i++)
            gltfParentIndices[i] = skeleton.Bones[i].ParentIndex;
        var engineParentIndices = BuildPsxEngineParentIndices(psxFile, boneCount);

        var bindWorld = MaterialiseBindWorldTranslations(skeleton, gltfParentIndices, boneCount);
        var engineLocalRotations = MaterialiseEngineLocalRotations(
            animation, boneCount, frameCount: animation.FrameCount, compose, rotScale);
        var engineWorldRaw = MaterialiseEngineWorldTranslations(
            animation, engineParentIndices, boneCount, animation.FrameCount, engineLocalRotations);
        var directWorld = MaterialiseExporterDirectWorldTranslations(
            animation, skeleton, gltfParentIndices, boneCount, frame, exporterDivisor,
            engineLocalRotations);
        var sourceBoneCount = Math.Min(
            Math.Min(sourceAnimation.BoneCount, sourcePsxFile.Objects.Count),
            renderAnimOrder.TargetPartToSourceSlot.Length);
        var sourceParentIndices = BuildPsxEngineParentIndices(sourcePsxFile, sourceBoneCount);
        var sourceEngineLocalRotations = MaterialiseEngineLocalRotations(
            sourceAnimation, sourceBoneCount, sourceAnimation.FrameCount, compose, rotScale);
        var sourceEngineWorldRaw = MaterialiseEngineWorldTranslations(
            sourceAnimation, sourceParentIndices, sourceBoneCount,
            sourceAnimation.FrameCount, sourceEngineLocalRotations);

        var glbSample = LoadGlbSample(glbPath, glbAnimIndex, time, skeleton, bones, out var glbInfo);

        PrintHeader(
            input, bankSource.DisplayName, bankKind, bank, animIndex, frame, animation,
            fps, time, psxFile, baseTranslationDivisor, transDivisorScale, exporterDivisor,
            remap);
        if (!string.IsNullOrWhiteSpace(glbInfo))
            AnsiConsole.MarkupLine(glbInfo);

        PrintTraceTable(
            skeleton, animation, bones, frame, gltfParentIndices, engineParentIndices, bindWorld, directWorld,
            engineWorldRaw, baseTranslationDivisor, exporterDivisor, glbSample);
        PrintSeparationHints(
            skeleton, bones, bindWorld, directWorld, engineWorldRaw, frame,
            baseTranslationDivisor, exporterDivisor, glbSample);
        if (vertexBounds)
        {
            PrintRenderAnimOrder(
                renderAnimOrder, skeleton, bones, engineParentIndices, sourceParentIndices);
            PrintVertexBounds(
                psxFile, skeleton, bones, bindWorld, engineLocalRotations,
                engineWorldRaw, frame, exporterDivisor, gltfParentIndices,
                renderAnimOrder, sourceEngineLocalRotations, sourceEngineWorldRaw,
                glbPath, glbAnimIndex, time);
        }

        return 0;
    }

    private static void PrintHeader(
        string input,
        string bankSource,
        string bankKind,
        PsxAnimationBankInfo bank,
        int animIndex,
        int frame,
        PsxAnimation animation,
        float fps,
        float time,
        PsxMeshFile psxFile,
        float baseTranslationDivisor,
        float transDivisorScale,
        float exporterDivisor,
        PsxAnimationBoneRemap? remap)
    {
        AnsiConsole.MarkupLine(
            $"[bold cyan]Character:[/] {Markup.Escape(Path.GetFileName(input))}  " +
            $"bones={psxFile.Objects.Count}  hierarchy={psxFile.HasHierarchy}");
        AnsiConsole.MarkupLine(
            $"[bold cyan]Animation bank:[/] {Markup.Escape(Path.GetFileName(bankSource))} " +
            $"({bankKind})  layout={bank.AnimFile.Layout}  revision={bank.AnimFile.FormatRevision}  " +
            $"entries={bank.AnimFile.Entries.Count}");
        if (remap is { IsIdentity: false })
        {
            AnsiConsole.MarkupLine(
                $"[grey]bone remap:[/] {remap.RemappedCount} source PSH slot(s) reordered for the target.");
        }

        AnsiConsole.MarkupLine(
            $"[bold cyan]Trace:[/] anim={animIndex} frame={frame}/{animation.FrameCount - 1} " +
            $"time={Format(time)}s fps={Format(fps)}");
        AnsiConsole.MarkupLine(
            $"[bold cyan]Divisors:[/] objectBind={Format(psxFile.TranslationDivisor)} " +
            $"animBase={Format(baseTranslationDivisor)} " +
            $"scale={Format(transDivisorScale)} activeExporter={Format(exporterDivisor)}");
    }

    private static void PrintTraceTable(
        ModelSkeleton skeleton,
        PsxAnimation animation,
        IReadOnlyList<int> bones,
        int frame,
        int[] gltfParentIndices,
        int[] engineParentIndices,
        Vector3[] bindWorld,
        Vector3[] directWorld,
        Vector3[,] engineWorldRaw,
        float baseTranslationDivisor,
        float exporterDivisor,
        IReadOnlyDictionary<int, Vector3>? glbSample)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("Bone World Translation Trace");
        table.AddColumn("Bone");
        table.AddColumn("Raw local");
        table.AddColumn("Bind GLB");
        table.AddColumn("Exporter local-delta");
        table.AddColumn("Engine active-div");
        table.AddColumn("Engine base-div");
        if (glbSample != null)
        {
            table.AddColumn("GLB sample");
            table.AddColumn("GLB-exp");
            table.AddColumn("GLB-eng");
        }

        foreach (var bone in bones)
        {
            var engineActive = EngineTarget(bindWorld[bone], engineWorldRaw, bone, frame, exporterDivisor);
            var engineBase = EngineTarget(bindWorld[bone], engineWorldRaw, bone, frame, baseTranslationDivisor);
            var rawLocal = animation.GetBoneTranslation(bone, frame);
            var label = $"{bone}:{skeleton.Bones[bone].Name}\ngltf={gltfParentIndices[bone]} engine={engineParentIndices[bone]}";
            var row = new List<string>
            {
                Markup.Escape(label),
                FormatVector(rawLocal),
                FormatVector(bindWorld[bone]),
                FormatVector(directWorld[bone]),
                FormatVector(engineActive),
                FormatVector(engineBase)
            };

            if (glbSample != null)
            {
                if (glbSample.TryGetValue(bone, out var glb))
                {
                    row.Add(FormatVector(glb));
                    row.Add(FormatDelta(glb - directWorld[bone]));
                    row.Add(FormatDelta(glb - engineActive));
                }
                else
                {
                    row.Add("[grey]missing node[/]");
                    row.Add("[grey]-[/]");
                    row.Add("[grey]-[/]");
                }
            }

            table.AddRow(row.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void PrintSeparationHints(
        ModelSkeleton skeleton,
        IReadOnlyList<int> bones,
        Vector3[] bindWorld,
        Vector3[] directWorld,
        Vector3[,] engineWorldRaw,
        int frame,
        float baseTranslationDivisor,
        float exporterDivisor,
        IReadOnlyDictionary<int, Vector3>? glbSample)
    {
        var interestingPairs = new[]
        {
            (A: 1, B: 16, Label: "right_shoe -> board"),
            (A: 4, B: 16, Label: "left_shoe -> board"),
            (A: 16, B: 17, Label: "board -> front_wheel"),
            (A: 16, B: 18, Label: "board -> back_wheel")
        };
        var selected = new HashSet<int>(bones);
        var table = new Table().Border(TableBorder.Simple).Title("Pair Distances");
        table.AddColumn("Pair");
        table.AddColumn("Bind");
        table.AddColumn("Exporter");
        table.AddColumn("Engine active-div");
        table.AddColumn("Engine base-div");
        if (glbSample != null)
            table.AddColumn("GLB");

        foreach (var pair in interestingPairs)
        {
            if (pair.A >= skeleton.Bones.Count || pair.B >= skeleton.Bones.Count)
                continue;
            if (!selected.Contains(pair.A) && !selected.Contains(pair.B))
                continue;

            var engineA = EngineTarget(bindWorld[pair.A], engineWorldRaw, pair.A, frame, exporterDivisor);
            var engineB = EngineTarget(bindWorld[pair.B], engineWorldRaw, pair.B, frame, exporterDivisor);
            var engineBaseA = EngineTarget(bindWorld[pair.A], engineWorldRaw, pair.A, frame, baseTranslationDivisor);
            var engineBaseB = EngineTarget(bindWorld[pair.B], engineWorldRaw, pair.B, frame, baseTranslationDivisor);
            var row = new List<string>
            {
                Markup.Escape(pair.Label),
                Format(Vector3.Distance(bindWorld[pair.A], bindWorld[pair.B])),
                Format(Vector3.Distance(directWorld[pair.A], directWorld[pair.B])),
                Format(Vector3.Distance(engineA, engineB)),
                Format(Vector3.Distance(engineBaseA, engineBaseB))
            };
            if (glbSample != null)
            {
                row.Add(glbSample.TryGetValue(pair.A, out var ga)
                        && glbSample.TryGetValue(pair.B, out var gb)
                    ? Format(Vector3.Distance(ga, gb))
                    : "[grey]-[/]");
            }

            table.AddRow(row.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static IReadOnlyDictionary<int, Vector3>? LoadGlbSample(
        string? glbPath,
        int glbAnimIndex,
        float time,
        ModelSkeleton skeleton,
        IReadOnlyList<int> bones,
        out string info)
    {
        info = "";
        if (string.IsNullOrWhiteSpace(glbPath))
            return null;

        if (!File.Exists(glbPath))
        {
            info = $"[yellow]Warning:[/] GLB not found: {Markup.Escape(glbPath)}";
            return null;
        }

        var model = ModelRoot.Load(glbPath);
        if ((uint)glbAnimIndex >= (uint)model.LogicalAnimations.Count)
        {
            info =
                $"[yellow]Warning:[/] GLB animation index {glbAnimIndex} is out of range; " +
                $"GLB has {model.LogicalAnimations.Count}.";
            return null;
        }

        var animation = model.LogicalAnimations[glbAnimIndex];
        var sample = new Dictionary<int, Vector3>();
        foreach (var bone in bones)
        {
            var boneName = skeleton.Bones[bone].Name;
            var node = model.LogicalNodes.FirstOrDefault(n =>
                string.Equals(n.Name, boneName, StringComparison.OrdinalIgnoreCase));
            if (node == null)
                continue;

            var world = GlbModelLoader.EvaluateAnimatedWorldMatrixForTesting(node, animation, time);
            sample[bone] = new Vector3(world.M41, world.M42, world.M43);
        }

        info =
            $"[bold cyan]GLB sample:[/] {Markup.Escape(Path.GetFileName(glbPath))} " +
            $"anim={glbAnimIndex} time={Format(time)}s nodes={sample.Count}/{bones.Count}";
        return sample;
    }

    private static void PrintVertexBounds(
        PsxMeshFile psxFile,
        ModelSkeleton skeleton,
        IReadOnlyList<int> bones,
        Vector3[] bindWorld,
        Quaternion[,] engineLocalRotations,
        Vector3[,] engineWorldRaw,
        int frame,
        float exporterDivisor,
        int[] gltfParentIndices,
        RenderAnimOrder renderAnimOrder,
        Quaternion[,] sourceEngineLocalRotations,
        Vector3[,] sourceEngineWorldRaw,
        string? glbPath,
        int glbAnimIndex,
        float time)
    {
        var boneCount = Math.Min(bindWorld.Length, skeleton.Bones.Count);
        var samples = CollectPsxBindVertexSamples(psxFile, boneCount);
        if (samples.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No PSX skinned vertex samples were available.");
            return;
        }

        var bindBounds = AccumulateBounds(samples, boneCount, static sample => sample.Position);
        var exporterNoTransMatrices = MaterialiseExporterNoTranslationWorldMatrices(
            skeleton, gltfParentIndices, boneCount, engineLocalRotations, frame);
        var exporterNoTransBounds = AccumulateBounds(samples, boneCount, sample =>
        {
            var bone = sample.BoneIndex;
            var skinMatrix = Matrix4x4.CreateTranslation(-bindWorld[bone])
                             * exporterNoTransMatrices[bone];
            return Vector3.Transform(sample.Position, skinMatrix);
        });
        var engineBindBounds = AccumulateBounds(samples, boneCount, sample =>
        {
            var bone = sample.BoneIndex;
            var rotation = ToGltfRotation(engineLocalRotations[bone, frame]);
            return bindWorld[bone] + Vector3.Transform(sample.Position - bindWorld[bone], rotation);
        });
        var engineAnimBounds = AccumulateBounds(samples, boneCount, sample =>
        {
            var bone = sample.BoneIndex;
            var rotation = ToGltfRotation(engineLocalRotations[bone, frame]);
            var target = EngineTarget(bindWorld[bone], engineWorldRaw, bone, frame, exporterDivisor);
            return target + Vector3.Transform(sample.Position - bindWorld[bone], rotation);
        });

        var glbBounds = LoadGlbVertexBounds(
            glbPath, glbAnimIndex, time, boneCount, out var glbInfo);
        if (!string.IsNullOrWhiteSpace(glbInfo))
            AnsiConsole.MarkupLine(glbInfo);

        var selected = new HashSet<int>(bones);
        var table = new Table().Border(TableBorder.Rounded)
            .Title("Per-Bone Vertex Bounds");
        table.AddColumn("Bone");
        table.AddColumn("Count");
        table.AddColumn("Bind");
        if (glbBounds != null)
            table.AddColumn("GLB");
        table.AddColumn("Export no-trans");
        table.AddColumn("Engine bindT");
        table.AddColumn("Engine animT");
        if (glbBounds != null)
            table.AddColumn("GLB-engine");

        for (var bone = 0; bone < boneCount; bone++)
        {
            if (!selected.Contains(bone)
                || bindBounds[bone].Count == 0)
            {
                continue;
            }

            var label = $"{bone}:{skeleton.Bones[bone].Name}";
            var row = new List<string>
            {
                Markup.Escape(label),
                bindBounds[bone].Count.ToString(CultureInfo.InvariantCulture),
                FormatBounds(bindBounds[bone])
            };

            if (glbBounds != null)
                row.Add(FormatBounds(glbBounds[bone]));

            row.Add(FormatBounds(exporterNoTransBounds[bone]));
            row.Add(FormatBounds(engineBindBounds[bone]));
            row.Add(FormatBounds(engineAnimBounds[bone]));

            if (glbBounds != null)
                row.Add(FormatBoundsDelta(glbBounds[bone], engineAnimBounds[bone]));

            table.AddRow(row.ToArray());
        }

        AnsiConsole.Write(table);

        PrintRenderPartVertexBounds(
            psxFile, skeleton, bones, bindWorld, engineLocalRotations,
            engineWorldRaw, frame, exporterDivisor, renderAnimOrder,
            sourceEngineLocalRotations, sourceEngineWorldRaw);
    }

    private static void PrintRenderAnimOrder(
        RenderAnimOrder order,
        ModelSkeleton skeleton,
        IReadOnlyList<int> bones,
        IReadOnlyList<int> targetParentIndices,
        IReadOnlyList<int> sourceParentIndices)
    {
        if (!string.IsNullOrWhiteSpace(order.Diagnostic))
            AnsiConsole.MarkupLine($"[yellow]render anim-order:[/] {Markup.Escape(order.Diagnostic)}");

        if (order.IsIdentity)
        {
            AnsiConsole.MarkupLine(
                "[bold cyan]Render anim-order:[/] identity target part -> same transform slot.");
            return;
        }

        var selected = new HashSet<int>(bones);
        var table = new Table().Border(TableBorder.Simple)
            .Title("Render Anim Order (Target Part -> Source Transform)");
        table.AddColumn("Target part");
        table.AddColumn("Target PSH");
        table.AddColumn("Source slot");
        table.AddColumn("Source PSH");
        table.AddColumn("Decoded remap");

        for (var target = 0; target < order.TargetPartToSourceSlot.Length; target++)
        {
            if (!selected.Contains(target))
                continue;

            var source = order.TargetPartToSourceSlot[target];
            var targetParent = target < targetParentIndices.Count
                ? targetParentIndices[target]
                : -1;
            var sourceParent = source >= 0 && source < sourceParentIndices.Count
                ? sourceParentIndices[source]
                : -1;
            var targetName = GetPshName(order.TargetPsh, target, skeleton.Bones[target].Name);
            var sourceName = source >= 0
                ? GetPshName(order.SourcePsh, source, $"bone_{source}")
                : "missing";
            table.AddRow(
                $"{target}:{Markup.Escape(skeleton.Bones[target].Name)}\nparent={targetParent}",
                Markup.Escape(targetName),
                source >= 0
                    ? $"{source.ToString(CultureInfo.InvariantCulture)}\nparent={sourceParent}"
                    : "[red]-[/]",
                Markup.Escape(sourceName),
                source == target
                    ? "identity"
                    : $"{source} -> {target}");
        }

        AnsiConsole.Write(table);
    }

    private static void PrintRenderPartVertexBounds(
        PsxMeshFile psxFile,
        ModelSkeleton skeleton,
        IReadOnlyList<int> bones,
        Vector3[] bindWorld,
        Quaternion[,] engineLocalRotations,
        Vector3[,] engineWorldRaw,
        int frame,
        float exporterDivisor,
        RenderAnimOrder renderAnimOrder,
        Quaternion[,] sourceEngineLocalRotations,
        Vector3[,] sourceEngineWorldRaw)
    {
        var partCount = Math.Min(
            Math.Min(skeleton.Bones.Count, bindWorld.Length),
            renderAnimOrder.TargetPartToSourceSlot.Length);
        var samples = CollectPsxRenderPartVertexSamples(psxFile, partCount);
        if (samples.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No PSX render part vertex samples were available.");
            return;
        }

        var targetFrame = Math.Clamp(frame, 0, engineWorldRaw.GetLength(1) - 1);
        var sourceFrame = Math.Clamp(frame, 0, sourceEngineWorldRaw.GetLength(1) - 1);
        var bindBounds = AccumulateRenderPartBounds(
            samples, partCount, static sample => sample.BindPosition);
        var targetOrderBounds = AccumulateRenderPartBounds(samples, partCount, sample =>
        {
            var part = sample.PartIndex;
            if (part >= engineWorldRaw.GetLength(0)
                || part >= engineLocalRotations.GetLength(0))
            {
                return sample.BindPosition;
            }

            var origin = EngineTarget(bindWorld[part], engineWorldRaw, part, targetFrame, exporterDivisor);
            return TransformRenderPartVertex(
                sample, engineLocalRotations[part, targetFrame], origin, divideLocalBy16: false);
        });
        var animOrderBounds = AccumulateRenderPartBounds(samples, partCount, sample =>
        {
            if (!TryGetRenderSourceSlot(renderAnimOrder, sample.PartIndex,
                    sourceEngineWorldRaw.GetLength(0), out var sourceSlot))
            {
                return sample.BindPosition;
            }

            var origin = EngineTarget(
                bindWorld[sample.PartIndex], sourceEngineWorldRaw, sourceSlot, sourceFrame, exporterDivisor);
            return TransformRenderPartVertex(
                sample, sourceEngineLocalRotations[sourceSlot, sourceFrame], origin, divideLocalBy16: false);
        });
        var animOrderSuperBounds = AccumulateRenderPartBounds(samples, partCount, sample =>
        {
            if (!TryGetRenderSourceSlot(renderAnimOrder, sample.PartIndex,
                    sourceEngineWorldRaw.GetLength(0), out var sourceSlot))
            {
                return sample.BindPosition;
            }

            var origin = EngineTarget(
                bindWorld[sample.PartIndex], sourceEngineWorldRaw, sourceSlot, sourceFrame, exporterDivisor);
            return TransformRenderPartVertex(
                sample, sourceEngineLocalRotations[sourceSlot, sourceFrame], origin, divideLocalBy16: true);
        });

        var selected = new HashSet<int>(bones);
        var table = new Table().Border(TableBorder.Rounded)
            .Title("PSX Render Part Vertex Bounds");
        table.AddColumn("Part");
        table.AddColumn("Count");
        table.AddColumn("Bind part-local");
        table.AddColumn("Target-order render");
        table.AddColumn("mAnimOrder render");
        table.AddColumn("v>>4 variant");
        table.AddColumn("order-target");
        table.AddColumn("v>>4-order");

        for (var part = 0; part < partCount; part++)
        {
            if (!selected.Contains(part) || bindBounds[part].Count == 0)
                continue;

            table.AddRow(
                $"{part}:{Markup.Escape(skeleton.Bones[part].Name)}",
                bindBounds[part].Count.ToString(CultureInfo.InvariantCulture),
                FormatBounds(bindBounds[part]),
                FormatBounds(targetOrderBounds[part]),
                FormatBounds(animOrderBounds[part]),
                FormatBounds(animOrderSuperBounds[part]),
                FormatBoundsDelta(animOrderBounds[part], targetOrderBounds[part]),
                FormatBoundsDelta(animOrderSuperBounds[part], animOrderBounds[part]));
        }

        AnsiConsole.Write(table);
    }

    private static bool TryGetRenderSourceSlot(
        RenderAnimOrder order,
        int targetPart,
        int sourceCount,
        out int sourceSlot)
    {
        sourceSlot = -1;
        if ((uint)targetPart >= (uint)order.TargetPartToSourceSlot.Length)
            return false;

        sourceSlot = order.TargetPartToSourceSlot[targetPart];
        return sourceSlot >= 0 && sourceSlot < sourceCount;
    }

    private static Vector3 TransformRenderPartVertex(
        PsxRenderPartVertexSample sample,
        Quaternion psxRotation,
        Vector3 origin,
        bool divideLocalBy16)
    {
        var local = divideLocalBy16
            ? sample.LocalPosition / 16f
            : sample.LocalPosition;
        return origin + Vector3.Transform(local, ToGltfRotation(psxRotation));
    }

    private static IReadOnlyList<PsxRenderPartVertexSample> CollectPsxRenderPartVertexSamples(
        PsxMeshFile psxFile,
        int partCount)
    {
        var samples = new List<PsxRenderPartVertexSample>();
        var lodVariants = BuildPsxLodVariantSet(psxFile);
        var alternateLeafObjects = PsxMeshSemantics.FindAlternateLeafObjectIndices(psxFile);
        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count && objectIndex < partCount; objectIndex++)
        {
            if (alternateLeafObjects.Contains(objectIndex))
                continue;

            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
            if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count || lodVariants.Contains(meshIndex))
                continue;

            var mesh = psxFile.Meshes[meshIndex];
            if (mesh.Faces.Count == 0)
                continue;

            var objectOffset = PsxMeshSemantics.GetObjectOffset(psxFile, psxFile.Objects[objectIndex]);
            foreach (var face in mesh.Faces)
            {
                foreach (var slot in EnumerateExportFaceSlots(face))
                {
                    var vertexIndex = GetPsxFaceVertexIndex(face, slot);
                    if (vertexIndex >= mesh.VertexCount || vertexIndex >= mesh.Vertices.Count)
                        continue;

                    var vertex = mesh.Vertices[(int)vertexIndex];
                    var localPosition = new Vector3(vertex.X, vertex.Y, vertex.Z);
                    if (PsxMeshSemantics.IsExactStitchedReference(vertex.Type))
                    {
                        var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex, vertexIndex);
                        if (resolved.AttachmentResolved)
                            localPosition = resolved.WorldPosition - objectOffset;
                    }

                    samples.Add(new PsxRenderPartVertexSample(
                        objectIndex,
                        PsxMeshSemantics.ToGltfPosition(localPosition),
                        PsxMeshSemantics.ToGltfPosition(objectOffset + localPosition)));
                }
            }
        }

        return samples;
    }

    private static BoundsAccumulator[] AccumulateRenderPartBounds(
        IReadOnlyList<PsxRenderPartVertexSample> samples,
        int partCount,
        Func<PsxRenderPartVertexSample, Vector3> transform)
    {
        var bounds = CreateBoundsArray(partCount);
        foreach (var sample in samples)
            AddToBounds(bounds, sample.PartIndex, transform(sample));

        return bounds;
    }

    private static BoundsAccumulator[]? LoadGlbVertexBounds(
        string? glbPath,
        int glbAnimIndex,
        float time,
        int boneCount,
        out string info)
    {
        info = "";
        if (string.IsNullOrWhiteSpace(glbPath))
            return null;

        if (!File.Exists(glbPath))
        {
            info = $"[yellow]Warning:[/] GLB not found for vertex bounds: {Markup.Escape(glbPath)}";
            return null;
        }

        var model = ModelRoot.Load(glbPath);
        if ((uint)glbAnimIndex >= (uint)model.LogicalAnimations.Count)
        {
            info =
                $"[yellow]Warning:[/] GLB animation index {glbAnimIndex} is out of range for vertex bounds; " +
                $"GLB has {model.LogicalAnimations.Count}.";
            return null;
        }

        var animation = model.LogicalAnimations[glbAnimIndex];
        var bounds = CreateBoundsArray(boneCount);
        var skinnedVertexCount = 0;

        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh == null || node.Skin == null)
                continue;

            var skin = node.Skin;
            var jointWorldTransforms = new Matrix4x4[skin.JointsCount];
            var inverseBindMatrices = new Matrix4x4[skin.JointsCount];
            for (var joint = 0; joint < skin.JointsCount; joint++)
            {
                var (jointNode, ibm) = skin.GetJoint(joint);
                jointWorldTransforms[joint] = jointNode.GetWorldMatrix(animation, time);
                inverseBindMatrices[joint] = ibm;
            }

            foreach (var prim in node.Mesh.Primitives)
            {
                var posAccessor = prim.GetVertexAccessor("POSITION");
                var jointsAccessor = prim.GetVertexAccessor("JOINTS_0");
                var weightsAccessor = prim.GetVertexAccessor("WEIGHTS_0");
                if (posAccessor == null || jointsAccessor == null || weightsAccessor == null)
                    continue;

                var positions = posAccessor.AsVector3Array();
                var joints = jointsAccessor.AsVector4Array();
                var weights = weightsAccessor.AsVector4Array();
                var count = Math.Min(positions.Count, Math.Min(joints.Count, weights.Count));
                for (var i = 0; i < count; i++)
                {
                    var joint = SelectDominantJoint(joints[i], weights[i]);
                    if (joint < 0 || joint >= boneCount)
                        continue;

                    var skinned = Vector3.Zero;
                    ApplyGlbJointWeight(
                        ref skinned, positions[i], (int)joints[i].X, weights[i].X,
                        jointWorldTransforms, inverseBindMatrices);
                    ApplyGlbJointWeight(
                        ref skinned, positions[i], (int)joints[i].Y, weights[i].Y,
                        jointWorldTransforms, inverseBindMatrices);
                    ApplyGlbJointWeight(
                        ref skinned, positions[i], (int)joints[i].Z, weights[i].Z,
                        jointWorldTransforms, inverseBindMatrices);
                    ApplyGlbJointWeight(
                        ref skinned, positions[i], (int)joints[i].W, weights[i].W,
                        jointWorldTransforms, inverseBindMatrices);

                    AddToBounds(bounds, joint, skinned);
                    skinnedVertexCount++;
                }
            }
        }

        info =
            $"[bold cyan]GLB vertex bounds:[/] {Markup.Escape(Path.GetFileName(glbPath))} " +
            $"anim={glbAnimIndex} time={Format(time)}s vertices={skinnedVertexCount}";
        return bounds;
    }

    private static void ApplyGlbJointWeight(
        ref Vector3 result,
        Vector3 position,
        int jointIndex,
        float weight,
        Matrix4x4[] jointWorldTransforms,
        Matrix4x4[] inverseBindMatrices)
    {
        if (weight <= 0f
            || jointIndex < 0
            || jointIndex >= jointWorldTransforms.Length
            || jointIndex >= inverseBindMatrices.Length)
        {
            return;
        }

        var skinMatrix = inverseBindMatrices[jointIndex] * jointWorldTransforms[jointIndex];
        result += Vector3.Transform(position, skinMatrix) * weight;
    }

    private static int SelectDominantJoint(Vector4 joints, Vector4 weights)
    {
        var bestJoint = (int)joints.X;
        var bestWeight = weights.X;
        if (weights.Y > bestWeight)
        {
            bestJoint = (int)joints.Y;
            bestWeight = weights.Y;
        }

        if (weights.Z > bestWeight)
        {
            bestJoint = (int)joints.Z;
            bestWeight = weights.Z;
        }

        if (weights.W > bestWeight)
        {
            bestJoint = (int)joints.W;
            bestWeight = weights.W;
        }

        return bestWeight > 0f ? bestJoint : -1;
    }

    private static IReadOnlyList<PsxBindVertexSample> CollectPsxBindVertexSamples(
        PsxMeshFile psxFile,
        int boneCount)
    {
        var samples = new List<PsxBindVertexSample>();
        var lodVariants = BuildPsxLodVariantSet(psxFile);
        var alternateLeafObjects = PsxMeshSemantics.FindAlternateLeafObjectIndices(psxFile);
        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            if (alternateLeafObjects.Contains(objectIndex))
                continue;

            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
            if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count || lodVariants.Contains(meshIndex))
                continue;

            var mesh = psxFile.Meshes[meshIndex];
            foreach (var face in mesh.Faces)
            {
                foreach (var slot in EnumerateExportFaceSlots(face))
                {
                    var vertexIndex = GetPsxFaceVertexIndex(face, slot);
                    if (vertexIndex >= mesh.VertexCount)
                        continue;

                    var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex, vertexIndex);
                    var jointIndex = resolved.SourceObjectIndex >= 0 ? resolved.SourceObjectIndex : objectIndex;
                    if (jointIndex < 0 || jointIndex >= boneCount)
                        continue;

                    samples.Add(new PsxBindVertexSample(
                        jointIndex,
                        PsxMeshSemantics.ToGltfPosition(resolved.WorldPosition)));
                }
            }
        }

        return samples;
    }

    private static IEnumerable<int> EnumerateExportFaceSlots(PsxFace face)
    {
        yield return 0;
        yield return 1;
        yield return 2;
        if (!face.IsQuad)
            yield break;

        yield return 1;
        yield return 3;
        yield return 2;
    }

    private static uint GetPsxFaceVertexIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => face.Index0
        };
    }

    private static HashSet<int> BuildPsxLodVariantSet(PsxMeshFile psxFile)
    {
        return psxFile.Meshes
            .Select(static mesh => (int)mesh.LodNextMeshIndex)
            .Where(index => index != ushort.MaxValue && index < psxFile.Meshes.Count)
            .ToHashSet();
    }

    private static BoundsAccumulator[] AccumulateBounds(
        IReadOnlyList<PsxBindVertexSample> samples,
        int boneCount,
        Func<PsxBindVertexSample, Vector3> transform)
    {
        var bounds = CreateBoundsArray(boneCount);
        foreach (var sample in samples)
            AddToBounds(bounds, sample.BoneIndex, transform(sample));

        return bounds;
    }

    private static BoundsAccumulator[] CreateBoundsArray(int count)
    {
        var bounds = new BoundsAccumulator[count];
        for (var i = 0; i < bounds.Length; i++)
            bounds[i] = BoundsAccumulator.Empty;
        return bounds;
    }

    private static void AddToBounds(BoundsAccumulator[] bounds, int index, Vector3 value)
    {
        if (index < 0 || index >= bounds.Length)
            return;

        var accumulator = bounds[index];
        accumulator.Include(value);
        bounds[index] = accumulator;
    }

    private static Matrix4x4[] MaterialiseExporterNoTranslationWorldMatrices(
        ModelSkeleton skeleton,
        int[] parentIndices,
        int boneCount,
        Quaternion[,] engineLocalRotations,
        int frame)
    {
        var localTranslations = new Vector3[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
            localTranslations[bone] = ExtractTranslation(skeleton.Bones[bone].LocalTransform);

        var localRotations = MaterialiseExporterLocalRotations(
            parentIndices, boneCount, engineLocalRotations, frame);
        return MaterialiseWorldMatrices(parentIndices, localTranslations, localRotations);
    }

    private static Quaternion[] MaterialiseExporterLocalRotations(
        int[] parentIndices,
        int boneCount,
        Quaternion[,] engineLocalRotations,
        int frame)
    {
        var localRotations = new Quaternion[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
        {
            var parent = parentIndices[bone];
            var psxRot = IsUsableParent(parent, bone, boneCount)
                ? Quaternion.Conjugate(engineLocalRotations[parent, frame])
                  * engineLocalRotations[bone, frame]
                : engineLocalRotations[bone, frame];
            localRotations[bone] = ToGltfRotation(psxRot);
        }

        return localRotations;
    }

    private static Matrix4x4[] MaterialiseWorldMatrices(
        int[] parentIndices,
        Vector3[] localTranslations,
        Quaternion[] localRotations)
    {
        var worldMatrices = new Matrix4x4[localTranslations.Length];
        var computed = new bool[localTranslations.Length];
        for (var bone = 0; bone < localTranslations.Length; bone++)
            MaterialiseWorldMatrix(parentIndices, localTranslations, localRotations, worldMatrices, computed, bone);

        return worldMatrices;
    }

    private static Matrix4x4 MaterialiseWorldMatrix(
        int[] parentIndices,
        Vector3[] localTranslations,
        Quaternion[] localRotations,
        Matrix4x4[] worldMatrices,
        bool[] computed,
        int bone)
    {
        if (computed[bone])
            return worldMatrices[bone];

        var local =
            Matrix4x4.CreateFromQuaternion(localRotations[bone])
            * Matrix4x4.CreateTranslation(localTranslations[bone]);
        var parent = parentIndices[bone];
        worldMatrices[bone] = IsUsableParent(parent, bone, localTranslations.Length)
            ? local * MaterialiseWorldMatrix(
                parentIndices, localTranslations, localRotations, worldMatrices, computed, parent)
            : local;
        computed[bone] = true;
        return worldMatrices[bone];
    }

    private static Vector3[] MaterialiseBindLocalTranslations(ModelSkeleton skeleton, int boneCount)
    {
        var local = new Vector3[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
            local[bone] = ExtractTranslation(skeleton.Bones[bone].LocalTransform);
        return local;
    }

    private static int[] BuildPsxEngineParentIndices(PsxMeshFile psxFile, int boneCount)
    {
        var parents = new int[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
        {
            var parent = bone < psxFile.Objects.Count
                ? psxFile.Objects[bone].ParentIndex
                : -1;
            parents[bone] = IsUsableParent(parent, bone, boneCount) ? parent : -1;
        }

        return parents;
    }

    private static Vector3[] MaterialiseBindWorldTranslations(
        ModelSkeleton skeleton,
        int[] parentIndices,
        int boneCount)
    {
        var bindLocal = MaterialiseBindLocalTranslations(skeleton, boneCount);
        var world = new Vector3[boneCount];
        var computed = new bool[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
            MaterialiseBindWorldTranslation(bindLocal, parentIndices, world, computed, bone);
        return world;
    }

    private static Vector3 MaterialiseBindWorldTranslation(
        Vector3[] bindLocal,
        int[] parentIndices,
        Vector3[] world,
        bool[] computed,
        int bone)
    {
        if (computed[bone])
            return world[bone];

        var parent = parentIndices[bone];
        world[bone] = IsUsableParent(parent, bone, bindLocal.Length)
            ? MaterialiseBindWorldTranslation(bindLocal, parentIndices, world, computed, parent)
              + bindLocal[bone]
            : bindLocal[bone];
        computed[bone] = true;
        return world[bone];
    }

    private static Quaternion[,] MaterialiseEngineLocalRotations(
        PsxAnimation animation,
        int boneCount,
        int frameCount,
        PsxRotationCompose compose,
        float rotationScale)
    {
        var rotations = new Quaternion[boneCount, frameCount];
        for (var bone = 0; bone < boneCount; bone++)
        {
            var animated = animation.IsRotationAnimated(bone);
            for (var frame = 0; frame < frameCount; frame++)
            {
                rotations[bone, frame] = animated
                    ? animation.GetBoneRotation(bone, frame, compose, rotationScale)
                    : Quaternion.Identity;
            }
        }

        return rotations;
    }

    private static Vector3[,] MaterialiseEngineWorldTranslations(
        PsxAnimation animation,
        int[] parentIndices,
        int boneCount,
        int frameCount,
        Quaternion[,] engineLocalRotations)
    {
        var world = new Vector3[boneCount, frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var computed = new bool[boneCount];
            for (var bone = 0; bone < boneCount; bone++)
            {
                MaterialiseEngineWorldTranslation(
                    animation, parentIndices, engineLocalRotations, world, computed, bone, frame);
            }
        }

        return world;
    }

    private static Vector3 MaterialiseEngineWorldTranslation(
        PsxAnimation animation,
        int[] parentIndices,
        Quaternion[,] engineLocalRotations,
        Vector3[,] world,
        bool[] computed,
        int bone,
        int frame)
    {
        if (computed[bone])
            return world[bone, frame];

        var rawTranslation = animation.GetBoneTranslation(bone, frame);
        var parent = parentIndices[bone];
        world[bone, frame] = IsUsableParent(parent, bone, world.GetLength(0))
            ? MaterialiseEngineWorldTranslation(
                  animation, parentIndices, engineLocalRotations, world, computed, parent, frame)
              + Vector3.Transform(rawTranslation, engineLocalRotations[parent, frame])
            : rawTranslation;

        computed[bone] = true;
        return world[bone, frame];
    }

    private static Vector3[] MaterialiseExporterDirectWorldTranslations(
        PsxAnimation animation,
        ModelSkeleton skeleton,
        int[] parentIndices,
        int boneCount,
        int frame,
        float translationDivisor,
        Quaternion[,] engineLocalRotations)
    {
        var localTranslations = new Vector3[boneCount];
        var localRotations = new Quaternion[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
        {
            var bindLocal = ExtractTranslation(skeleton.Bones[bone].LocalTransform);
            var anchor = animation.GetBoneTranslation(bone, 0) / translationDivisor;
            var current = animation.GetBoneTranslation(bone, frame) / translationDivisor;
            localTranslations[bone] =
                bindLocal + PsxMeshSemantics.ToGltfPosition(current - anchor);

            var parent = parentIndices[bone];
            var psxRot = IsUsableParent(parent, bone, boneCount)
                ? Quaternion.Conjugate(engineLocalRotations[parent, frame])
                  * engineLocalRotations[bone, frame]
                : engineLocalRotations[bone, frame];
            localRotations[bone] = ToGltfRotation(psxRot);
        }

        var worldMatrices = new Matrix4x4[boneCount];
        var computed = new bool[boneCount];
        var result = new Vector3[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
        {
            var world = MaterialiseExporterDirectWorldMatrix(
                parentIndices, localTranslations, localRotations, worldMatrices, computed, bone);
            result[bone] = new Vector3(world.M41, world.M42, world.M43);
        }

        return result;
    }

    private static Matrix4x4 MaterialiseExporterDirectWorldMatrix(
        int[] parentIndices,
        Vector3[] localTranslations,
        Quaternion[] localRotations,
        Matrix4x4[] worldMatrices,
        bool[] computed,
        int bone)
    {
        if (computed[bone])
            return worldMatrices[bone];

        var local =
            Matrix4x4.CreateFromQuaternion(localRotations[bone])
            * Matrix4x4.CreateTranslation(localTranslations[bone]);
        var parent = parentIndices[bone];
        worldMatrices[bone] = IsUsableParent(parent, bone, localTranslations.Length)
            ? local * MaterialiseExporterDirectWorldMatrix(
                parentIndices, localTranslations, localRotations, worldMatrices, computed, parent)
            : local;
        computed[bone] = true;
        return worldMatrices[bone];
    }

    private static Vector3 EngineTarget(
        Vector3 bindWorld,
        Vector3[,] engineWorldRaw,
        int bone,
        int frame,
        float divisor)
    {
        var anchor = engineWorldRaw[bone, 0] / divisor;
        var current = engineWorldRaw[bone, frame] / divisor;
        return bindWorld + PsxMeshSemantics.ToGltfPosition(current - anchor);
    }

    private static RenderAnimOrder BuildRenderAnimOrder(
        AssetSource bankSource,
        AssetSource inputSource,
        int boneCount,
        PsxAnimationBoneRemap? remap,
        string? remapDiagnostic)
    {
        var sourcePsh = PsxAnimationBoneMap.TryReadPsh(bankSource);
        var targetPsh = PsxAnimationBoneMap.TryReadPsh(inputSource);
        var targetToSource = Enumerable.Range(0, boneCount).ToArray();
        if (IsSameAsset(bankSource, inputSource))
        {
            return new RenderAnimOrder(
                targetToSource, sourcePsh, targetPsh, Diagnostic: null);
        }

        if (remap == null)
        {
            var diagnostic = string.IsNullOrWhiteSpace(remapDiagnostic)
                ? "no PSH remap available for external bank; assuming identity render order"
                : $"{remapDiagnostic}; assuming identity render order";
            return new RenderAnimOrder(targetToSource, sourcePsh, targetPsh, diagnostic);
        }

        Array.Fill(targetToSource, -1);
        var duplicateTargets = 0;
        var sourceLimit = Math.Min(boneCount, remap.SourceToTarget.Count);
        for (var source = 0; source < sourceLimit; source++)
        {
            var target = remap.SourceToTarget[source];
            if (target < 0 || target >= boneCount)
                continue;

            if (targetToSource[target] >= 0)
            {
                duplicateTargets++;
                continue;
            }

            targetToSource[target] = source;
        }

        var missingTargets = 0;
        for (var target = 0; target < targetToSource.Length; target++)
        {
            if (targetToSource[target] >= 0)
                continue;

            targetToSource[target] = target;
            missingTargets++;
        }

        string? diagnosticText = null;
        if (missingTargets > 0 || duplicateTargets > 0)
        {
            diagnosticText =
                $"render order filled {missingTargets} missing target(s) with identity; " +
                $"ignored {duplicateTargets} duplicate source target(s)";
        }

        return new RenderAnimOrder(targetToSource, sourcePsh, targetPsh, diagnosticText);
    }

    private static bool IsSameAsset(AssetSource a, AssetSource b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (!string.IsNullOrWhiteSpace(a.FileSystemPath)
            && !string.IsNullOrWhiteSpace(b.FileSystemPath))
        {
            return string.Equals(
                Path.GetFullPath(a.FileSystemPath),
                Path.GetFullPath(b.FileSystemPath),
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPshName(PshFile? psh, int index, string fallback)
    {
        return psh?.GetBoneName(index) ?? fallback;
    }

    private static IReadOnlyList<int> ResolveBones(string? bonesSpec, int boneCount)
    {
        if (string.IsNullOrWhiteSpace(bonesSpec))
            bonesSpec = "0,1,4,16-18";

        var result = new SortedSet<int>();
        foreach (var rawPart in bonesSpec.Split(
                     ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = rawPart.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                if (!int.TryParse(rawPart[..dash], out var start)
                    || !int.TryParse(rawPart[(dash + 1)..], out var end))
                {
                    continue;
                }

                if (end < start)
                    (start, end) = (end, start);
                for (var i = start; i <= end; i++)
                {
                    if ((uint)i < (uint)boneCount)
                        result.Add(i);
                }

                continue;
            }

            if (int.TryParse(rawPart, out var bone) && (uint)bone < (uint)boneCount)
                result.Add(bone);
        }

        return result.ToList();
    }

    private static IReadOnlySet<int>? ResolveOptionalBones(string? bonesSpec, int boneCount)
    {
        if (string.IsNullOrWhiteSpace(bonesSpec))
            return null;

        var bones = ResolveBones(bonesSpec, boneCount);
        return bones.Count == 0
            ? null
            : new HashSet<int>(bones);
    }

    private static PsxRotationCompose ParseRotCompose(string s)
    {
        return Enum.TryParse<PsxRotationCompose>(s, true, out var compose)
            ? compose
            : PsxRotationCompose.YXZ;
    }

    private static Vector3 ExtractTranslation(Matrix4x4 matrix)
    {
        return new Vector3(matrix.M41, matrix.M42, matrix.M43);
    }

    private static Quaternion ToGltfRotation(Quaternion psxRot)
    {
        var gltf = new Quaternion(psxRot.X, -psxRot.Y, -psxRot.Z, psxRot.W);
        var lengthSquared = gltf.LengthSquared();
        return lengthSquared > 0f && float.IsFinite(lengthSquared)
            ? Quaternion.Normalize(gltf)
            : Quaternion.Identity;
    }

    private static bool IsUsableParent(int parent, int bone, int boneCount)
    {
        return parent >= 0 && parent < boneCount && parent != bone;
    }

    private readonly record struct PsxBindVertexSample(int BoneIndex, Vector3 Position);

    private readonly record struct PsxRenderPartVertexSample(
        int PartIndex,
        Vector3 LocalPosition,
        Vector3 BindPosition);

    private sealed record RenderAnimOrder(
        int[] TargetPartToSourceSlot,
        PshFile? SourcePsh,
        PshFile? TargetPsh,
        string? Diagnostic)
    {
        public bool IsIdentity
        {
            get
            {
                for (var i = 0; i < TargetPartToSourceSlot.Length; i++)
                {
                    if (TargetPartToSourceSlot[i] != i)
                        return false;
                }

                return true;
            }
        }
    }

    private struct BoundsAccumulator
    {
        public static BoundsAccumulator Empty => new()
        {
            Min = new Vector3(float.PositiveInfinity),
            Max = new Vector3(float.NegativeInfinity)
        };

        public int Count { get; private set; }
        public Vector3 Min { get; private set; }
        public Vector3 Max { get; private set; }
        public Vector3 Center => Count == 0 ? Vector3.Zero : (Min + Max) * 0.5f;
        public Vector3 Extents => Count == 0 ? Vector3.Zero : Max - Min;

        public void Include(Vector3 value)
        {
            if (Count == 0)
            {
                Min = value;
                Max = value;
            }
            else
            {
                Min = Vector3.Min(Min, value);
                Max = Vector3.Max(Max, value);
            }

            Count++;
        }
    }

    private static string FormatBounds(BoundsAccumulator bounds)
    {
        if (bounds.Count == 0)
            return "[grey]-[/]";

        return $"c {FormatVector(bounds.Center)}\ne {FormatVector(bounds.Extents)}";
    }

    private static string FormatBoundsDelta(BoundsAccumulator a, BoundsAccumulator b)
    {
        if (a.Count == 0 || b.Count == 0)
            return "[grey]-[/]";

        return $"c {FormatDelta(a.Center - b.Center)}\ne {FormatDelta(a.Extents - b.Extents)}";
    }

    private static string FormatVector(Vector3 value)
    {
        return $"{Format(value.X)}, {Format(value.Y)}, {Format(value.Z)}";
    }

    private static string FormatDelta(Vector3 value)
    {
        var max = Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        var color = max < 0.01f ? "green" : max < 0.25f ? "yellow" : "red";
        return $"[{color}]{Markup.Escape(FormatVector(value))}[/]";
    }

    private static string Format(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
