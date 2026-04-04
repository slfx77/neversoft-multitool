namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

/// <summary>
///     Atomic linking a frame index to a geometry index.
/// </summary>
public sealed class RwAtomic
{
    public required int FrameIndex { get; init; }
    public required int GeometryIndex { get; init; }
    public required int Flags { get; init; }
    public RwSkinData? SkinData { get; init; }
}
