namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsImageTransferTargetAudit
{
    public uint Dbp { get; set; }
    public uint Dbw { get; set; }
    public uint Dpsm { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dsax { get; set; }
    public int Dsay { get; set; }
    public long Transfers { get; set; }
    public long Bytes { get; set; }
}
