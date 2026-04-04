namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal sealed class ReplayEmittedVertex
{
    public required int SourceIndex { get; init; }
    public required ReplayVertexSource Source { get; init; }
    public required int FullOutputAddress { get; init; }
    public required byte OutputAddress { get; init; }
    public required bool IsNoKick { get; init; }
    public required bool IsDuplicate { get; init; }
}
