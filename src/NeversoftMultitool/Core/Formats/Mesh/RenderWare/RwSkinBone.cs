using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

/// <summary>
///     Single bone from RW Skin PLG (0x0116).
///     76 bytes: id(u32) + index(u32) + flags(u32) + 4×4 inverse bind matrix.
/// </summary>
public readonly struct RwSkinBone(int id, int index, int flags, Matrix4x4 inverseBindMatrix)
{
    public int Id { get; } = id;
    public int Index { get; } = index;
    public int Flags { get; } = flags;
    public Matrix4x4 InverseBindMatrix { get; } = inverseBindMatrix;
}
