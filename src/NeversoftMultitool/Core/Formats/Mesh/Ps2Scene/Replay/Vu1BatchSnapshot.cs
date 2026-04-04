namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal sealed class Vu1BatchSnapshot
{
    public required int SetupIndex { get; init; }
    public required int CommandOffset { get; init; }
    public required ushort Imm { get; init; }
    public required int PreTops { get; init; }
    public required int Xtop { get; init; }
    public required int PostTops { get; init; }
    public required int Dbf { get; init; }
    public required int MinWrittenAddress { get; init; }
    public required int MaxWrittenAddress { get; init; }
    public required int MinWriteWindowStart { get; init; }
    public required Vu1Qword[] XtopWindow { get; init; }
    public required Vu1Qword[] MinWriteWindow { get; init; }
    public required int KickBaseWindowStart { get; init; }
    public required Vu1Qword[] KickBaseWindow { get; init; }
    public required GifKickPacket ParserTag { get; init; }
    public required VifUnpackCommand[] Unpacks { get; init; }
}
