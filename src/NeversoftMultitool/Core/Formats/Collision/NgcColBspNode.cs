namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>
///     One node of a GameCube collision axis-aligned BSP tree
///     (THUG source <c>CCollBSPNode</c>, NGC big-endian disk layout).
///     Axis 0-2 is an interior split node; axis 3 is a leaf carrying
///     object-relative face indices.
/// </summary>
public sealed class NgcColBspNode
{
    /// <summary>Split axis: 0 = X, 1 = Y, 2 = Z, 3 = leaf.</summary>
    public required int Axis { get; init; }

    public bool IsLeaf => Axis == 3;

    /// <summary>
    ///     Interior nodes: the raw signed 32-bit split word whose low two bits
    ///     carry <see cref="Axis" />.
    /// </summary>
    public int RawSplitWord { get; init; }

    /// <summary>
    ///     Interior nodes: split point in world units
    ///     (<c>(raw &gt;&gt; 2) / 16</c>, the engine's collision sub-inch
    ///     fixed-point precision).
    /// </summary>
    public float SplitPoint { get; init; }

    /// <summary>
    ///     Interior nodes: whether the first stored child is the greater-side
    ///     branch (low bit of the child pointer word).
    /// </summary>
    public bool LeftIsGreater { get; init; }

    public NgcColBspNode? Less { get; init; }

    public NgcColBspNode? Greater { get; init; }

    /// <summary>Leaves: face indices relative to the owning object's first face.</summary>
    public ushort[]? LeafFaceIndices { get; init; }

    public int CountNodes()
    {
        return 1 + (Less?.CountNodes() ?? 0) + (Greater?.CountNodes() ?? 0);
    }

    public int CountLeaves()
    {
        if (IsLeaf) return 1;
        return (Less?.CountLeaves() ?? 0) + (Greater?.CountLeaves() ?? 0);
    }
}
