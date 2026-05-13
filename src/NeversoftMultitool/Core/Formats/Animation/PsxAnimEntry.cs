namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One animation slot's entry in the per-PSX-file animation table:
///     where its compressed stream lives in the pool, and how many frames it spans.
/// </summary>
public sealed record PsxAnimEntry(int PoolOffset, int FrameCount);
