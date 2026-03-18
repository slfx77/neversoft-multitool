namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal sealed class ReplayContextWrite
{
    public required VifUnpackCommand Unpack { get; init; }
    public required int[] WriteAddresses { get; init; }
    public required Vu1Qword[] Words { get; init; }
}
