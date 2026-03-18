using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Frame in the RW frame hierarchy (FrameList).
///     Contains local rotation matrix (3×3), position, and parent index.
/// </summary>
public sealed class RwFrame
{
    public required Matrix4x4 LocalTransform { get; init; }
    public required int ParentIndex { get; init; }
    public required int Flags { get; init; }
}
