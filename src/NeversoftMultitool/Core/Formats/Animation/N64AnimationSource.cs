namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One anonymous direct or compressed slot embedded in a carved N64 model shell.
///     The source keeps the owning bundle so GUI selection can route the exact
///     index back through <c>MeshImportRequest.N64AnimationIndices</c>.
/// </summary>
internal sealed class N64AnimationSource(
    AssetSource modelSource,
    int animationIndex,
    int frameCount) : AssetSource
{
    public AssetSource ModelSource { get; } = modelSource;

    public int AnimationIndex { get; } = animationIndex;

    public int FrameCount { get; } = frameCount;

    public override string DisplayName =>
        $"{Path.GetFileName(ModelSource.EntryName)}::anim_{AnimationIndex}";

    public override string EntryName => $"anim_{AnimationIndex}";

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
