namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     <see cref="AssetSource" /> adapter for an animation slot embedded in a
///     PS1 <c>.psx</c> animation bank. The bank may be the selected character
///     file itself or an external shared animation file such as THPS2's
///     <c>sk2anim.psx</c>.
/// </summary>
internal sealed class PsxAnimationSource : AssetSource
{
    private readonly string _displayName;

    public PsxAnimationSource(
        AssetSource bankSource,
        int animIndex,
        int frameCount,
        int targetBoneCount,
        string? displayName = null,
        PsxAnimationBoneRemap? boneRemap = null)
    {
        BankSource = bankSource;
        AnimIndex = animIndex;
        FrameCount = frameCount;
        TargetBoneCount = targetBoneCount;
        BoneRemap = boneRemap;
        _displayName = displayName ?? $"{Path.GetFileName(bankSource.EntryName)}::anim_{animIndex}";
    }

    public AssetSource BankSource { get; }

    public int AnimIndex { get; }

    public int FrameCount { get; }

    public int TargetBoneCount { get; }

    public PsxAnimationBoneRemap? BoneRemap { get; }

    public override string DisplayName => _displayName;
    public override string EntryName => $"anim_{AnimIndex}";

    public override string? FileSystemPath => BankSource.FileSystemPath;

    /// <summary>
    ///     Reads the parent <c>.psx</c> bank in full. Callers that only want one
    ///     animation slot should use <see cref="Decode" />.
    /// </summary>
    public override byte[] ReadBytes()
    {
        return BankSource.ReadBytes();
    }

    /// <summary>
    ///     Parses the parent <c>.psx</c> bank, locates the animation table, and
    ///     decodes the slot identified by <see cref="AnimIndex" /> into a
    ///     ready-to-render <see cref="PsxAnimation" />.
    /// </summary>
    public PsxAnimation Decode()
    {
        return PsxAnimationBank.DecodeSlot(BankSource, TargetBoneCount, AnimIndex, BoneRemap);
    }

    public override bool CompanionExists(string nameWithExtension)
    {
        return BankSource.CompanionExists(nameWithExtension);
    }

    public override byte[]? TryReadCompanion(string nameWithExtension)
    {
        return BankSource.TryReadCompanion(nameWithExtension);
    }

    public override byte[]? TryReadCompanion(
        string stem, IReadOnlyList<string> extensions, IReadOnlyList<string>? subdirs = null)
    {
        return BankSource.TryReadCompanion(stem, extensions, subdirs);
    }
}
