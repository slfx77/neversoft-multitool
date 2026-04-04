namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal sealed class VifUnpackCommand
{
    public required int CommandOffset { get; init; }
    public required int DataOffset { get; init; }
    public required int Vn { get; init; }
    public required int Vl { get; init; }
    public required int Num { get; init; }
    public required int Address { get; init; }
    public required bool Flg { get; init; }
    public required bool Usn { get; init; }
    public required byte CycleCl { get; init; }
    public required byte CycleWl { get; init; }
    public required int EffectiveAddress { get; init; }
    public required int EndAddress { get; init; }
    public required int[] WriteAddresses { get; init; }
}
