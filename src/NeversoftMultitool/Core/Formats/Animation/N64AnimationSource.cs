namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One direct or compressed slot embedded in a carved N64 model shell. The
///     source keeps the owning bundle so GUI selection can route the exact
///     index back through <c>MeshImportRequest.N64AnimationIndices</c>.
///     <para>
///         Slots are anonymous in the file. Where the cart's own
///         <c>tricks.bin</c> uniquely owns a slot, <paramref name="trickName" />
///         carries its real name; everything else keeps the synthetic label.
///     </para>
/// </summary>
internal sealed class N64AnimationSource(
    AssetSource modelSource,
    int animationIndex,
    int frameCount,
    string? trickName = null) : AssetSource
{
    public AssetSource ModelSource { get; } = modelSource;

    public int AnimationIndex { get; } = animationIndex;

    public int FrameCount { get; } = frameCount;

    /// <summary>The slot's label — a trick name where one is known.</summary>
    public string Label { get; } = trickName ?? $"anim_{animationIndex}";

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
