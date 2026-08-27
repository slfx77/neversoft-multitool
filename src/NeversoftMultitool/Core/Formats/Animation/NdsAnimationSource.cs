namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One clip of a Vicarious Visions DS model's animation, carried so a GUI
///     selection can route the exact clip index back through
///     <c>MeshImportRequest.NdsAnimationIndices</c>.
///
///     DS clips are anonymous — the container names them only by their ordinal
///     within their model's library — so the label is synthetic, exactly as the N64
///     and GBA routes label theirs where no trick table names a slot.
/// </summary>
internal sealed class NdsAnimationSource(
    AssetSource modelSource,
    int clipIndex,
    int frames) : AssetSource
{
    public AssetSource ModelSource { get; } = modelSource;

    public int ClipIndex { get; } = clipIndex;

    public int Frames { get; } = frames;

    public string Label { get; } = $"anim_{clipIndex}";

    public override string DisplayName =>
        $"{Path.GetFileName(ModelSource.EntryName)}::{Label}";

    public override string EntryName => Label;

    public override string? FileSystemPath => ModelSource.FileSystemPath;

    public override byte[] ReadBytes()
    {
        return ModelSource.ReadBytes();
    }

    public override bool CompanionExists(string nameWithExtension)
    {
        return ModelSource.CompanionExists(nameWithExtension);
    }

    public override byte[]? TryReadCompanion(string nameWithExtension)
    {
        return ModelSource.TryReadCompanion(nameWithExtension);
    }

    public override byte[]? TryReadCompanion(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs = null)
    {
        return ModelSource.TryReadCompanion(stem, extensions, subdirs);
    }
}
