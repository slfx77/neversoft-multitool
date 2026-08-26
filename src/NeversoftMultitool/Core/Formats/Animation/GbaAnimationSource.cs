namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One clip of the THPS2 GBA skater's morph animation, carried so GUI
///     selection can route the exact clip index back through
///     <c>MeshImportRequest.GbaAnimationIndices</c>. Clips are anonymous in the
///     ROM's own tables, so the label is synthetic unless a proven trick name
///     uniquely owns the clip.
/// </summary>
internal sealed class GbaAnimationSource(
    AssetSource modelSource,
    int clipIndex,
    int tickCount,
    string? trickName = null) : AssetSource
{
    public AssetSource ModelSource { get; } = modelSource;

    public int ClipIndex { get; } = clipIndex;

    public int TickCount { get; } = tickCount;

    public string Label { get; } = trickName ?? $"anim_{clipIndex}";

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
