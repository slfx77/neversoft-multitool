namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsPresentedCircuitAudit
{
    public required int Circuit { get; init; }
    public required bool Enabled { get; init; }
    public string? Key { get; set; }
    public uint Fbp { get; set; }
    public uint Fbw { get; set; }
    public uint Psm { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dbx { get; set; }
    public int Dby { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int Dw { get; set; }
    public int Dh { get; set; }
    public int Magh { get; set; }
    public int Magv { get; set; }
    public long NonBlackPixels { get; set; }
}
