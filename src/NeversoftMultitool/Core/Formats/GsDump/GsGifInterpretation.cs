namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsGifInterpretation
{
    public required GsGifAudit Gif { get; init; }
    public required GsRenderAudit Render { get; init; }
    public required byte[] DirectPixels { get; init; }
    public required byte[] Pixels { get; init; }
    public required List<GsFramebufferSnapshot> FramebufferSnapshots { get; init; }
    public required Dictionary<ulong, long> XyzByTex0 { get; init; }
}
