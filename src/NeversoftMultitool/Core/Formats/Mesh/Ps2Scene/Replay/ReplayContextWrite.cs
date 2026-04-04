namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal sealed class ReplayContextWrite
{
    public required VifUnpackCommand Unpack { get; init; }
    public required int[] WriteAddresses { get; init; }
    public required Vu1Qword[] Words { get; init; }
}
