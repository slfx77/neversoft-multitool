namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal sealed class ThawReplayBatch
{
    public required int SetupIndex { get; init; }
    public required bool IsPreambleBatch { get; init; }
    public required int FirstCommandOffset { get; init; }
    public required int PositionOffset { get; init; }
    public required int NormalOffset { get; init; }
    public required int UvAdcOffset { get; init; }
    public required int VertexCount { get; init; }
    public required int OutputVertexCount { get; init; }
    public required ReplayVertexSource[] VertexSources { get; init; }
    public required ReplayVertexSource[] RawVertexSources { get; init; }
    public required ReplayEmittedVertex[] EmittedVertices { get; init; }
    public required ReplayContextWrite[] ContextWrites { get; init; }
    public required PostBatchElement[] PostBatchElements { get; init; }
    public required GifKickPacket OutputKickPacket { get; init; }
    public required VifReplayCommandTrace[] CommandTrace { get; init; }
    public required Vu1BatchSnapshot Snapshot { get; init; }
}
