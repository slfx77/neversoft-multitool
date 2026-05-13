namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed record GsDumpPacket(
    GsDumpPacketKind Kind,
    GsTransferPath? Path,
    byte[] Data);
