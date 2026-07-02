namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Options that control how PSX animation tracks are emitted to glTF.
///     Used to A/B test rotation/translation conventions while the format is
///     being calibrated.
/// </summary>
/// <param name="SkipTranslation">
///     When true (default), per-bone translation channels are omitted. The
///     current rotation-only path keeps bind placement stable while animation
///     parity is calibrated. When enabled, translation keys are emitted as
///     bind-anchored deltas so frame 0 keeps the skeleton's bind placement
///     instead of treating PSX Tx/Ty/Tz values as absolute glTF node positions.
/// </param>
/// <param name="LegacyRotationChain">
///     When true, emit each bone's per-frame rotation directly without
///     piecewise-rigid correction (glTF parent chain composes through). When
///     false (default), pre-divide each bone's rotation by its parent's so
///     glTF's chain reproduces the engine's piecewise-rigid result.
/// </param>
/// <param name="RotationScale">
///     Diagnostic multiplier applied to decoded Euler angles before quaternion
///     composition. Default 1.0 preserves the runtime PSY-Q angle convention;
///     lower values are useful for validating suspected over-amplification
///     without changing the decoder.
/// </param>
/// <param name="TranslationBoneFilter">
///     Optional diagnostic filter used with translation emission. When set,
///     only the listed bone indices get translation channels.
/// </param>
/// <param name="TranslationDivisorScale">
///     Multiplier applied to the raw animation translation divisor when
///     translation emission is enabled. The default 16 matches the runtime's
///     Super SMatrix translation right-shift without changing bind object
///     placement.
/// </param>
/// <param name="AbsoluteTranslation">
///     When true, translation channels replace the bind translation with the
///     decoded PSX Tx/Ty/Tz value. Default false keeps the safer frame-0-
///     anchored diagnostic delta path.
/// </param>
/// <param name="EngineWorldTranslation">
///     When true, recursively compose PSX translation tracks the way
///     <c>Decomp_GetAnimTransform</c> does, then solve those world-space targets
///     back into glTF local translation keys. This remains an opt-in diagnostic
///     path: it improves some numeric board/foot distances but can visibly
///     worsen the authored pose on current exports.
/// </param>
/// <param name="SourceHierarchyTranslation">
///     Diagnostic companion to <see cref="EngineWorldTranslation" />. When a clip
///     was decoded from an external PSX animation bank, use that bank's parsed
///     hierarchy parent table, remapped into target bone order, for recursive
///     translation composition. This mirrors the runtime's active pHierarchy
///     source without changing mesh bind hierarchy.
/// </param>
public sealed record PsxAnimationOptions(
    bool SkipRotation = false,
    bool SkipTranslation = true,
    PsxRotationCompose RotationCompose = PsxRotationCompose.YXZ,
    float Fps = 10f,
    bool LegacyRotationChain = false,
    float RotationScale = 1f,
    IReadOnlySet<int>? TranslationBoneFilter = null,
    float TranslationDivisorScale = 16f,
    bool AbsoluteTranslation = false,
    bool EngineWorldTranslation = false,
    bool SourceHierarchyTranslation = false);
