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
                "Frame rate for time-base conversion (default: 30, the engine's native rate — " +
                "UpdateFrame advances one frame per 30Hz tick at the default mAnimSpeed of 1.0). " +
                "Lower values slow the preview for inspection.",
            DefaultValueFactory = _ => new PsxAnimationOptions().Fps
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
            Description =
                "Diagnostic: skip translation tracks (bones keep bind placement). Translations " +
                "are emitted by default per the engine fixed-point contract."
        };
        var oneShotOption = new Option<bool>("--one-shot")
        {
            Description =
                "Expand tween-compressed clips with the RunAnim one-shot end clamp (hold the last " +
                "keyframe). Default is the CycleAnim loop expansion: the final tween interval blends " +
                "toward frame 0 for a seamless loop, matching the engine's dominant character-anim mode."
        };
        var transBonesOption = new Option<string?>("--trans-bones")
        {
            Description =
                "Diagnostic: only emit translation tracks for this comma/range " +
                "bone list (for example 16 or 16-18)."
        };
        var transDivisorScaleOption = new Option<float>("--trans-divisor-scale")
        {
            Description =
                "Diagnostic: multiply the contract translation divisor (the " +
                "vertex ScaleDivisor). Default 1 per the engine fixed-point contract — anim s16 " +
                "translations share the model-vertex unit; use 16 to reproduce the old " +
                "double-shifted exports.",
            DefaultValueFactory = _ => new PsxAnimationOptions().TranslationDivisorScale
        };
        var transAbsoluteOption = new Option<bool>("--trans-absolute")
        {
            Description =
                "Emit PSX Tx/Ty/Tz as absolute node translations (default, " +
                "matching the engine's SMatrix.t). Pass 'false' for the legacy frame-0-anchored " +
                "bind-delta diagnostic.",
            DefaultValueFactory = _ => new PsxAnimationOptions().AbsoluteTranslation
        };
        var transEngineWorldOption = new Option<bool>("--trans-engine-world")
        {
            Description =
                "Diagnostic: force the explicit engine-world translation path (compose like " +
                "Decomp_GetAnimTransform, solve back to glTF locals). Engages automatically when " +
                "an external bank's hierarchy differs from the character's."
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
        command.Options.Add(noTransOption);
        command.Options.Add(oneShotOption);
        command.Options.Add(transBonesOption);
        command.Options.Add(transDivisorScaleOption);
        command.Options.Add(transAbsoluteOption);
        command.Options.Add(transEngineWorldOption);
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
            var noTrans = parseResult.GetValue(noTransOption);
            var oneShot = parseResult.GetValue(oneShotOption);
            var transBones = parseResult.GetValue(transBonesOption);
            var transDivisorScale = parseResult.GetValue(transDivisorScaleOption);
            var transAbsolute = parseResult.GetValue(transAbsoluteOption);
            var transEngineWorld = parseResult.GetValue(transEngineWorldOption);
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
                SkipTranslation: noTrans,
                RotationCompose: ParseRotCompose(rotCompose ?? "yxz"),
                Fps: fps,
                LegacyRotationChain: legacyChain,
                RotationScale: SanitizeRotationScale(rotScale),
                TranslationBoneFilter: translationBoneFilter,
                TranslationDivisorScale: SanitizePositiveScale(transDivisorScale, "--trans-divisor-scale"),
                AbsoluteTranslation: transAbsolute,
                EngineWorldTranslation: transEngineWorld,
                OneShot: oneShot);
            return Task.FromResult(PsxAnimExportRunner.Run(
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
}
