namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal sealed class VifReplayCommandTrace
{
    public required int CommandOffset { get; init; }
    public required ushort Imm { get; init; }
    public required byte RawCommand { get; init; }
    public required VifReplayCommandKind Kind { get; init; }
    public required VifReplayRegisters Before { get; init; }
    public required VifReplayRegisters After { get; init; }
    public VifUnpackCommand? Unpack { get; init; }
}
