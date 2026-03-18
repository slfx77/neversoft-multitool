namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal sealed class ReplayBatchBuilder
{
    public readonly List<VifReplayCommandTrace> Commands = [];
    public readonly List<ReplayContextWrite> ContextWrites = [];
    public readonly List<PostBatchElement> PostBatchElements = [];
    public readonly List<VifUnpackCommand> Unpacks = [];

    public int FirstCommandOffset { get; set; } = -1;
    public int PositionOffset { get; set; } = -1;
    public int NormalOffset { get; set; } = -1;
    public int UvAdcOffset { get; set; } = -1;
    public int VertexCount { get; set; }
    public VifUnpackCommand? PositionUnpack { get; set; }
    public VifUnpackCommand? NormalUnpack { get; set; }
    public VifUnpackCommand? UvAdcUnpack { get; set; }

    public bool HasReplayActivity => Commands.Count > 0 || Unpacks.Count > 0 || PostBatchElements.Count > 0;
    public bool HasVertices => PositionOffset >= 0 && VertexCount > 0;

    public ThawReplayBatch Build(
        byte[] data,
        Vu1Memory memory,
        int setupIndex,
        Vu1BatchSnapshot snapshot,
        bool isPreambleBatch)
    {
        var outputKickPacket = ThawPs2ReplayEngine.DecodeOutputKickPacket(PostBatchElements);
        var vertexSources = ThawPs2ReplayVertexDecoder.DecodeVertexSources(
            memory,
            PositionUnpack,
            NormalUnpack,
            UvAdcUnpack,
            VertexCount);
        var rawVertexSources = ThawPs2ReplayVertexDecoder.DecodeRawVertexSources(
            data,
            PositionOffset,
            NormalOffset,
            UvAdcOffset,
            VertexCount);

        return new ThawReplayBatch
        {
            SetupIndex = setupIndex,
            IsPreambleBatch = isPreambleBatch,
            FirstCommandOffset = FirstCommandOffset,
            PositionOffset = PositionOffset,
            NormalOffset = NormalOffset,
            UvAdcOffset = UvAdcOffset,
            VertexCount = VertexCount,
            OutputVertexCount = outputKickPacket.Nloop,
            VertexSources = vertexSources,
            RawVertexSources = rawVertexSources,
            EmittedVertices = ThawPs2ReplayVertexDecoder.BuildEmittedVertices(vertexSources),
            ContextWrites = [.. ContextWrites],
            PostBatchElements = [.. PostBatchElements],
            OutputKickPacket = outputKickPacket,
            CommandTrace = [.. Commands],
            Snapshot = snapshot
        };
    }
}
