namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsTextureCorrelationAudit
{
    public int UniqueRuntimeTex0 { get; set; }
    public int ResolvedTex0 { get; set; }
    public List<GsTex0CorrelationRow> TopRuntimeTex0 { get; set; } = [];
}
