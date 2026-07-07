namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Options that control how PSX animation tracks are emitted to glTF.
///     Used to A/B test rotation/translation conventions while the format is
///     being calibrated.
/// </summary>
/// <param name="SkipTranslation">
///     When true, per-bone translation channels are omitted (rotation-only
///     diagnostic export, bind placement retained). When false (default),
///     translation keys are emitted per the engine contract: the decoded s16
///     Tx/Ty/Tz values ARE the pose (Decomp_GetAnimTransform copies them into
///     SMatrix.t raw), in the same 1/16-world unit as model vertices, so they
///     are emitted absolute at the vertex ScaleDivisor. Clips whose
///     translation streams are entirely zero keep bind placement. See
///     tools/diagnostics/psx-anim-format.md (fixed-point contract).
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
///     Diagnostic multiplier applied on top of the contract translation
///     divisor (the vertex ScaleDivisor). Anim s16 translations share the
///     model-vertex unit (world×16); ScaleDivisor already contains the
///     runtime's &gt;&gt;4, so the correct scale is 1 (default). The old
///     default of 16 double-applied the shift and produced translations 16×
///     too small.
/// </param>
/// <param name="AbsoluteTranslation">
///     When true (default), translation channels carry the decoded PSX
///     Tx/Ty/Tz values directly — matching the engine, which rebuilds every
///     bone origin from anim data each frame with no bind fallback. Absolute
///     values at the vertex divisor land exactly in bind-offset units
///     (s16 = fp12 &gt;&gt; 8). False keeps the legacy frame-0-anchored
///     bind-delta path for A/B comparison against older exports.
/// </param>
/// <param name="EngineWorldTranslation">
///     When true, force the explicit engine-world path: recursively compose
///     PSX translation tracks the way <c>Decomp_GetAnimTransform</c> does,
///     then solve those world-space targets back into glTF local translation
///     keys. This engages AUTOMATICALLY whenever a clip's translation
///     hierarchy (e.g. an external anim bank's PSH hierarchy, mirroring the
///     runtime's pHierarchy) differs from the glTF skeleton's parent chain —
///     for matching hierarchies the default local path composes identically
///     through glTF's own parent chain, so the flag only matters for
///     flat/unparented skeletons and numeric diagnostics.
/// </param>
public sealed record PsxAnimationOptions(
    bool SkipRotation = false,
    bool SkipTranslation = false,
    PsxRotationCompose RotationCompose = PsxRotationCompose.YXZ,
    float Fps = 30f,
    bool LegacyRotationChain = false,
    float RotationScale = 1f,
    IReadOnlySet<int>? TranslationBoneFilter = null,
    float TranslationDivisorScale = 1f,
    bool AbsoluteTranslation = true,
    bool EngineWorldTranslation = false);
