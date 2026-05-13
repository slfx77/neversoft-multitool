using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Hierarchy link entry: sector-to-parent bone relationship with transform matrix.
///     Present in MDL files with multiple sectors (vehicles, multi-part objects).
/// </summary>
public sealed class XbxLink
{
    public uint SectorChecksum { get; init; }
    public uint ParentChecksum { get; init; }
    public ushort Index { get; init; }
    public Matrix4x4 Transform { get; init; }
}
