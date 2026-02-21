namespace NeversoftMultitool.Core.Formats.Video;

public sealed class SfdConvertResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? OutputPath { get; init; }
}
