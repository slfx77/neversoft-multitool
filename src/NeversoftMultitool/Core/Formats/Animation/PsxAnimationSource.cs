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
    private readonly string _psxPath;
    private readonly int _animIndex;

    public PsxAnimationSource(string psxPath, int animIndex, int frameCount)
    {
        _psxPath = psxPath;
        _animIndex = animIndex;
        FrameCount = frameCount;
    }

    public int AnimIndex => _animIndex;
    public int FrameCount { get; }
    public string PsxFilePath => _psxPath;

    public override string DisplayName => $"{Path.GetFileName(_psxPath)}::anim_{_animIndex}";
    public override string EntryName => $"anim_{_animIndex}";

    /// <summary>
    ///     Reads the parent <c>.psx</c> file in full. Callers that only want one
    ///     animation slot should prefer <see cref="Decode" /> instead.
    /// </summary>
    public override byte[] ReadBytes() => File.ReadAllBytes(_psxPath);

    /// <summary>
    ///     Parses the parent <c>.psx</c>, locates the animation table, and
    ///     decodes the slot identified by <see cref="AnimIndex" /> into a
    ///     ready-to-render <see cref="PsxAnimation" />.
    /// </summary>
    public PsxAnimation Decode()
    {
        var data = File.ReadAllBytes(_psxPath);
        var psxFile = PsxMeshFile.Parse(data)
                      ?? throw new InvalidDataException($"PSX file has no parseable mesh data: {_psxPath}");

        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd)
                       ?? throw new InvalidDataException($"PSX file has no recognizable animation table: {_psxPath}");

        if (_animIndex < 0 || _animIndex >= animFile.Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(AnimIndex),
                $"Anim index {_animIndex} out of range (0..{animFile.Entries.Count - 1}).");

        var entry = animFile.Entries[_animIndex];
        var slice = animFile.Pool.Span[entry.PoolOffset..];
        return PsxAnimDecoder.Decode(slice, psxFile.Objects.Count, entry.FrameCount);
    }

    public override bool CompanionExists(string nameWithExtension) => false;
    public override byte[]? TryReadCompanion(string nameWithExtension) => null;
    public override byte[]? TryReadCompanion(
        string stem, IReadOnlyList<string> extensions, IReadOnlyList<string>? subdirs = null) => null;
    public override string? FileSystemPath => _psxPath;
}
