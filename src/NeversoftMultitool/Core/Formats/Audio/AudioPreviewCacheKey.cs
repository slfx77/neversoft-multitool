using System.Runtime.CompilerServices;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Session-local preview identity. Parent entries are compared by reference so
///     two same-named files or archive entries cannot share a cached conversion.
/// </summary>
internal readonly struct AudioPreviewCacheKey : IEquatable<AudioPreviewCacheKey>
{
    private readonly object? _parentIdentity;

    public AudioPreviewCacheKey(object parentIdentity, int? sampleIndex, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(parentIdentity);
        _parentIdentity = parentIdentity;
        SampleIndex = sampleIndex;
        SampleRate = sampleRate;
    }

    public int? SampleIndex { get; }
    public int SampleRate { get; }

    public bool Equals(AudioPreviewCacheKey other) =>
        ReferenceEquals(_parentIdentity, other._parentIdentity)
        && SampleIndex == other.SampleIndex
        && SampleRate == other.SampleRate;

    public override bool Equals(object? obj) => obj is AudioPreviewCacheKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            _parentIdentity is null ? 0 : RuntimeHelpers.GetHashCode(_parentIdentity),
            SampleIndex,
            SampleRate);

    public static bool operator ==(AudioPreviewCacheKey left, AudioPreviewCacheKey right) => left.Equals(right);
    public static bool operator !=(AudioPreviewCacheKey left, AudioPreviewCacheKey right) => !left.Equals(right);
}
