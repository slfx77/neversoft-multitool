using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Single bone in a PS2 skeleton.
/// </summary>
public sealed class Ps2Bone
{
    public required uint NameChecksum { get; init; }
    public required uint ParentChecksum { get; init; }
    public required uint FlipChecksum { get; init; }

    /// <summary>Resolved index of the parent bone (-1 for root).</summary>
    public required int ParentIndex { get; init; }

    /// <summary>Local rotation (parent-relative) from the .ske file.</summary>
    public required Quaternion LocalRotation { get; init; }

    /// <summary>Local translation (parent-relative) from the .ske file.</summary>
    public required Vector3 LocalTranslation { get; init; }

    /// <summary>
    ///     Inverse bind matrix computed from the neutral pose hierarchy.
    ///     Transforms from model space to bone-local space.
    /// </summary>
    public required Matrix4x4 InverseBindMatrix { get; init; }
}
