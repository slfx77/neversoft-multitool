namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Lightweight metadata about a SKA file extracted from its header without
///     decoding keyframes. Produced by <see cref="SkaFile.TryProbe"/>.
/// </summary>
internal sealed record SkaProbeResult(float Duration, int? BoneCount);
