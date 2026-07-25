namespace NeversoftMultitool.CLI;

/// <summary>Located monolithic anim table: chunk base + entry table.</summary>
internal sealed record PsxAnimHierLocation(
    long Base,
    int NumStreams,
    int[] FrameCounts,
    int[] PoolOffsets);
