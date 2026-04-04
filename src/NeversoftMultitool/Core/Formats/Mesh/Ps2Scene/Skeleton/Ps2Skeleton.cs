namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

/// <summary>
///     Parsed PS2 skeleton file (.ske.ps2).
///     Contains bone hierarchy, neutral pose, and inverse bind matrices.
///     Format: version(u32) + flags(u32) + numBones(i32) + 3×name tables + neutral poses.
/// </summary>
public sealed class Ps2Skeleton
{
    public required int Version { get; init; }
    public required int Flags { get; init; }
    public required Ps2Bone[] Bones { get; init; }

    /// <summary>True if this is a female skeleton (flags bit 0).</summary>
    public bool IsFemale => (Flags & 1) != 0;
}
