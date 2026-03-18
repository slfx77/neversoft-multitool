namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Per-mesh attribute flags from THUG source mesh.h.
/// </summary>
[Flags]
public enum Ps2MeshFlags : uint
{
    Texture = 1 << 0,
    Colours = 1 << 1,
    Normals = 1 << 2,
    St16 = 1 << 3,
    Skinned = 1 << 4
}
