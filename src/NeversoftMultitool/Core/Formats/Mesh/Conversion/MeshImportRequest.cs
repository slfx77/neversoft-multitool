using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class MeshImportRequest
{
    public required AssetSource Source { get; init; }
    public required string FileName { get; init; }
    public required string OutputStem { get; init; }
    public required ModelSourceKind SourceKind { get; init; }
    public Ps2SceneSubFormat Ps2SubFormat { get; init; }
    public bool HasPlacedPsxCompanion { get; init; }
    public string? TexturePath { get; init; }
    public string? SkeletonPath { get; init; }
    public string? DdxPath { get; init; }
    public string? PsxPath { get; init; }
    public string? DdmTexturePath { get; init; }
    public bool PsxFlatSkeleton { get; init; }

    /// <summary>
    ///     Optional PS1 light-rig preset name (see <c>PsxEngineLight.Presets</c>).
    ///     When set, engine-lit faces are shaded with that rig exactly as the
    ///     console would; when null they keep their authored colours for the
    ///     viewer to light. The file says WHICH faces the engine lights but never
    ///     WHICH light — that is runtime context — so this is always the caller's
    ///     choice and never inferred.
    /// </summary>
    public string? PsxLightPreset { get; init; }
    public IReadOnlySet<int>? PsxFlatBoneIndices { get; init; }

    /// <summary>
    ///     Optional visibility selections keyed by
    ///     <see cref="ModelVisibilityGroup.Id" />. Missing and unknown keys are
    ///     ignored, so a caller can retain selections while changing assets.
    ///     The parser uses each source-authored default when this is null or a
    ///     group has no entry.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? VisibilityOverrides { get; init; }

    /// <summary>
    ///     Include supported level-object companions when the selected level has
    ///     one. This includes Spider-Man PSX <c>*_g.psx</c> object banks and
    ///     proven THPS traffic supers; script-created traffic remains behind its
    ///     generated, default-disabled visibility snapshot.
    /// </summary>
    public bool IncludeLevelObjects { get; init; } = true;

    public WorldzoneTimeOfDay WorldzoneTimeOfDay { get; init; } =
        WorldzoneTimeOfDay.All;

    public float WorldzoneScale { get; init; } = 1f;

    /// <summary>
    ///     When set, worldzone conversion writes triage diagnostics into this
    ///     directory: per-leaf rejection reasons, emitted-leaf GS state with
    ///     texture-resolution tags, and the texture catalog's debug dump.
    ///     Null (the default) changes nothing — pure opt-in diagnostics.
    /// </summary>
    public string? WorldzoneDebugDirectory { get; init; }

    /// <summary>
    ///     Options for embedding PSX character animations into the document. When
    ///     null, no animation tracks are populated. When set, the parser also
    ///     consults <see cref="PsxDecodedAnimations" /> for the pre-decoded slot
    ///     list to embed.
    /// </summary>
    public PsxAnimationOptions? PsxAnimationOptions { get; init; }

    /// <summary>
    ///     Pre-decoded PSX animation slots passed in by the caller (CLI / GUI).
    ///     Decoupling decode from populate lets the caller report per-slot
    ///     diagnostics (layout, decode failures) before the IR is built.
    /// </summary>
    public IReadOnlyList<(string Name, PsxAnimation Animation)>? PsxDecodedAnimations { get; init; }

    /// <summary>
    ///     Rich PSX animation entries with optional per-clip metadata. When set,
    ///     this takes precedence over <see cref="PsxDecodedAnimations" />.
    /// </summary>
    public IReadOnlyList<PsxAnimationClip>? PsxAnimationClips { get; init; }

    /// <summary>
    ///     Optional exact embedded N64 0x2A/0x2C slot selection. Null or empty
    ///     keeps ordinary export static; a concrete list is used by the GUI
    ///     Animations pane for selected-clip output. N64 animation remains
    ///     fail-closed unless geometry proves global G_MTX addressing or a
    ///     complete payload profile proves placement-relative addressing.
    /// </summary>
    public IReadOnlyList<int>? N64AnimationIndices { get; init; }

    /// <summary>
    ///     Explicit CLI opt-in for every eligible embedded N64 0x2A/0x2C slot.
    ///     Kept separate from <see cref="N64AnimationIndices" /> so null and
    ///     empty never acquire overloaded meanings.
    /// </summary>
    public bool IncludeAllN64Animations { get; init; }

    /// <summary>
    ///     Selects the RunAnim one-shot clamp instead of the default CycleAnim
    ///     wrap when expanding tween-compressed N64 clips. Scoped to N64 because
    ///     PSX callers pre-decode and carry the same choice on
    ///     <see cref="Core.Formats.Animation.PsxAnimationOptions.OneShot" />.
    ///     Reaches only DIRECT (0x2A) clips — compressed 0x2C slots store every
    ///     frame and have no end-of-clip branch to select.
    /// </summary>
    public bool N64AnimationOneShot { get; init; }

    /// <summary>
    ///     Optional exact GBA skater clip selection (indices into the ROM's clip
    ///     table). Null or empty keeps ordinary export static and byte-identical;
    ///     a concrete list is used by the GUI Animations pane for selected-clip
    ///     output. Out-of-range or authored-empty indices are skipped fail-closed.
    /// </summary>
    public IReadOnlyList<int>? GbaAnimationIndices { get; init; }

    /// <summary>
    ///     Explicit CLI opt-in for every non-empty GBA skater clip. Kept separate
    ///     from <see cref="GbaAnimationIndices" /> so null and empty never acquire
    ///     overloaded meanings.
    /// </summary>
    public bool IncludeAllGbaAnimations { get; init; }

    /// <summary>
    ///     Pre-decoded SKA animation slots, populated into <see cref="ModelDocument.Animations" />
    ///     by the PS2 Scene and RW DFF parsers. Null = no animations to embed.
    /// </summary>
    public IReadOnlyList<(string Name, SkaAnimation Animation)>? SkaAnimations { get; init; }

    /// <summary>
    ///     Optional exact-QbKey mapping from the skeleton that authored
    ///     <see cref="SkaAnimations" /> to the target mesh skeleton. This map is
    ///     created explicitly by a CLI or GUI caller; null retains the existing
    ///     same-rig index binding.
    /// </summary>
    public SkaQbKeyBoneMap? SkaQbKeyBoneMap { get; init; }

    /// <summary>
    ///     Optional pre-loaded skeleton override for PS2 Scene or XbxScene parsing.
    ///     When set, the parser uses this instead of re-loading from
    ///     <see cref="SkeletonPath" />.
    ///     Lets callers like <c>SkaCommand</c> apply THPS4 V1 default-pose enrichment
    ///     upstream and preserve it through the parser.
    /// </summary>
    public Ps2Skeleton? PreparedSkeleton { get; init; }

    /// <summary>
    ///     Which of a DS model's clips to bake, by their index in its own library.
    ///     Kept separate from the N64 and GBA lists so null and empty never acquire
    ///     overloaded meanings across platforms.
    /// </summary>
    public IReadOnlyList<int>? NdsAnimationIndices { get; init; }

    /// <summary>Bake every applicable clip the model's library holds.</summary>
    public bool IncludeAllNdsAnimations { get; init; }
}
