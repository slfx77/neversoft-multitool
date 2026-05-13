using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     <see cref="AssetSource" /> adapter for an animation slot embedded in a
///     PS1 character <c>.psx</c> file. PSX animations don't live in separate
///     <c>.ska</c>-style files, so the source carries the parent file path plus
///     the animation slot index and decodes on demand.
/// </summary>
internal sealed class PsxAnimationSource : AssetSource
{
    public PsxAnimationSource(string psxPath, int animIndex, int frameCount)
    {
        PsxFilePath = psxPath;
        AnimIndex = animIndex;
        FrameCount = frameCount;
    }

    public int AnimIndex { get; }

    public int FrameCount { get; }
    public string PsxFilePath { get; }

    public override string DisplayName => $"{Path.GetFileName(PsxFilePath)}::anim_{AnimIndex}";
    public override string EntryName => $"anim_{AnimIndex}";

    public override string? FileSystemPath => PsxFilePath;

    /// <summary>
    ///     Reads the parent <c>.psx</c> file in full. Callers that only want one
    ///     animation slot should prefer <see cref="Decode" /> instead.
    /// </summary>
    public override byte[] ReadBytes()
    {
        return File.ReadAllBytes(PsxFilePath);
    }

    /// <summary>
    ///     Parses the parent <c>.psx</c>, locates the animation table, and
    ///     decodes the slot identified by <see cref="AnimIndex" /> into a
    ///     ready-to-render <see cref="PsxAnimation" />.
    /// </summary>
    public PsxAnimation Decode()
    {
        var data = File.ReadAllBytes(PsxFilePath);
        var psxFile = PsxMeshFile.Parse(data)
                      ?? throw new InvalidDataException($"PSX file has no parseable mesh data: {PsxFilePath}");

        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd)
                       ?? throw new InvalidDataException(
                           $"PSX file has no recognizable animation table: {PsxFilePath}");

        if (AnimIndex < 0 || AnimIndex >= animFile.Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(AnimIndex),
                $"Anim index {AnimIndex} out of range (0..{animFile.Entries.Count - 1}).");

        var entry = animFile.Entries[AnimIndex];
        var slice = animFile.Pool.Span[entry.PoolOffset..];
        return PsxAnimDecoder.Decode(slice, psxFile.Objects.Count, entry.FrameCount);
    }

    public override bool CompanionExists(string nameWithExtension)
    {
        return false;
    }

    public override byte[]? TryReadCompanion(string nameWithExtension)
    {
        return null;
    }

    public override byte[]? TryReadCompanion(
        string stem, IReadOnlyList<string> extensions, IReadOnlyList<string>? subdirs = null)
    {
        return null;
    }
}
