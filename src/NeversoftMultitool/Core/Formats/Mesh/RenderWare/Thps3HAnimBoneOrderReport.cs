namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

internal readonly record struct Thps3HAnimBoneOrderReport(
    Thps3HAnimBoneOrderStatus IdStatus,
    int[]? IdPermutation,
    Thps3HAnimBoneOrderStatus IndexStatus,
    int[]? IndexPermutation)
{
    public bool IsExact =>
        IdStatus == Thps3HAnimBoneOrderStatus.Exact &&
        IndexStatus == Thps3HAnimBoneOrderStatus.Exact;

    public string ToDisplayString()
    {
        return $"id={Format(IdStatus, IdPermutation)}, index={Format(IndexStatus, IndexPermutation)}";
    }

    private static string Format(Thps3HAnimBoneOrderStatus status, int[]? permutation)
    {
        if (status != Thps3HAnimBoneOrderStatus.UsablePermutation || permutation == null)
            return status.ToString().ToLowerInvariant();

        return $"usable-permutation [{string.Join(",", permutation)}]";
    }
}
