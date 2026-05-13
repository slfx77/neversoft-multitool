namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsDumpAuditReport
{
    public required string InputPath { get; init; }
    public string? ScreenshotPath { get; init; }
    public string? EmbeddedScreenshotPath { get; init; }
    public string? RawDirectRenderPath { get; init; }
    public string? RawRenderPath { get; init; }
    public string? DirectRenderPath { get; init; }
    public string? RenderPath { get; init; }
    public string? DirectDiffPath { get; init; }
    public string? DiffPath { get; init; }
    public string? MaterialDumpPath { get; init; }
    public string? TextureDumpDirectory { get; init; }
    public uint Crc { get; init; }
    public string Serial { get; init; } = "";
    public int StateVersion { get; init; }
    public int StateSize { get; init; }
    public int RegisterSnapshotSize { get; init; }
    public int ScreenshotWidth { get; init; }
    public int ScreenshotHeight { get; init; }
    public int PacketCount { get; init; }
    public Dictionary<string, long> PacketTypeCounts { get; init; } = [];
    public Dictionary<string, GsTransferStats> TransferStats { get; init; } = [];
    public GsGifAudit Gif { get; init; } = new();
    public GsRenderAudit Render { get; init; } = new();
    public GsPixelDiffStats? DirectPixelDiff { get; init; }
    public GsPixelDiffStats? PixelDiff { get; init; }
    public GsTextureCorrelationAudit? TextureCorrelation { get; init; }
}
