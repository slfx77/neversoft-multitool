namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsGifAudit
{
    public long ProcessedVu1Packets { get; set; }
    public long SkippedTransferPackets { get; set; }
    public long GifTagCount { get; set; }
    public long XyzWriteCount { get; set; }
    public int UniqueTex0Count { get; set; }
    public Dictionary<string, long> RegisterWrites { get; set; } = [];
    public Dictionary<string, long> XyzWritesByPrimitive { get; set; } = [];
    public List<GsTex0AuditRow> TopTex0ByXyz { get; set; } = [];
}
