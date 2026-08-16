namespace NeversoftMultitool.Core.Formats.Video;

public sealed class SfdConvertResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? OutputPath { get; init; }

    /// <summary>
    ///     Frames whose decode failed and were written as black so one bad
    ///     frame does not abort the conversion. Reported because the
    ///     substitution is otherwise invisible: a stream that fails on every
    ///     frame produces a wholly black video and still returns
    ///     <see cref="Success" />.
    /// </summary>
    public int BlackFramesSubstituted { get; init; }
}
