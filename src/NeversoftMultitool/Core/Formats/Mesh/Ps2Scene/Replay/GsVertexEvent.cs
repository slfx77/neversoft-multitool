namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal sealed class GsVertexEvent
{
    public required int OutputIndex { get; init; }
    public required int FullOutputAddress { get; init; }
    public required byte OutputAddress { get; init; }
    public required GsVertexEventKind Kind { get; init; }
    public required ReplayVertexSource? VertexSource { get; init; }
    public required bool IsNoKick { get; init; }
    public required bool IsBufferedCarry { get; init; }
}
