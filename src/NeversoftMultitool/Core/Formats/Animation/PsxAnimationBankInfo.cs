namespace NeversoftMultitool.Core.Formats.Animation;

internal sealed record PsxAnimationBankInfo(
    AssetSource Source,
    PsxAnimFile AnimFile,
    int BoneCount,
    bool MatchesTargetBoneCount);
