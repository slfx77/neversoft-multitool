namespace NeversoftMultitool.Core.Formats.Audio;

public sealed class AudioConvertResult
{
    public bool Success { get; init; }
    public int SamplesWritten { get; init; }
    public string? ErrorMessage { get; init; }
}
