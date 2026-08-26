using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Explicit skeleton that authored selected SKA tracks. The source is fully
///     consumed at selection time so GUI state does not retain an archive handle.
/// </summary>
internal sealed record SkaAnimationSourceRig(
    string SourceDisplayName,
    Ps2Skeleton Skeleton)
{
    public int BoneCount => Skeleton.Bones.Length;

    public static SkaAnimationSourceRig Load(AssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SkaAnimationSourceRig(source.DisplayName, SkeletonAssetLoader.Load(source));
    }
}

/// <summary>
///     Immutable binding decision shared by discovery filtering and final model
///     construction. Null source rig preserves the historical same-rig index path.
/// </summary>
internal sealed record SkaAnimationBindingPlan(
    int ExpectedTrackCount,
    SkaQbKeyBoneMap? BoneMap)
{
    public static SkaAnimationBindingPlan Create(
        Ps2Skeleton target,
        SkaAnimationSourceRig? sourceRig)
    {
        ArgumentNullException.ThrowIfNull(target);

        return sourceRig == null
            ? new SkaAnimationBindingPlan(target.Bones.Length, null)
            : new SkaAnimationBindingPlan(
                sourceRig.BoneCount,
                SkaQbKeyBoneMap.Create(sourceRig.Skeleton, target));
    }

    public bool MatchesTrackCount(int? trackCount) =>
        !trackCount.HasValue || trackCount.Value == ExpectedTrackCount;
}

/// <summary>
///     Pure operation-state projection used by the WinUI animation panel. All
///     source-changing controls are disabled together while an operation owns
///     the panel, then recomputed from durable state when that operation ends.
/// </summary>
internal readonly record struct AnimationPanelOperationControlState(
    bool AddExternalSourcesEnabled,
    bool ChooseSourceRigEnabled,
    bool ChooseArchiveSourceRigEnabled,
    bool ClearSourceRigEnabled)
{
    /// <param name="isEmbeddedOnlyCharacter">
    ///     A character whose clips live only inside its own source (N64 bundles,
    ///     GBA characters). External animation sources cannot apply to it, so the
    ///     add-folder/add-archive controls stay disabled.
    /// </param>
    public static AnimationPanelOperationControlState Create(
        bool characterReady,
        bool isEmbeddedOnlyCharacter,
        bool isPs2SceneCharacter,
        bool targetSkeletonKnown,
        bool sourceRigSelected,
        bool operationActive)
    {
        var idleReady = characterReady && !operationActive;
        var canChooseRig = idleReady && isPs2SceneCharacter && targetSkeletonKnown;
        return new AnimationPanelOperationControlState(
            idleReady && !isEmbeddedOnlyCharacter,
            canChooseRig,
            canChooseRig,
            idleReady && isPs2SceneCharacter && sourceRigSelected);
    }
}
