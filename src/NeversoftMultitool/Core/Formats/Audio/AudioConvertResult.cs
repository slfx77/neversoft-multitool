namespace NeversoftMultitool.Core.Formats.Audio;

public sealed class AudioConvertResult
{
    public bool Success { get; init; }
    public int SamplesWritten { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Set when the file structurally is not the format its extension claims, so it was
    ///     passed over rather than failing. Extensions are shared across unrelated formats —
    ///     22 of the corpus's 35 <c>.seq</c> files are a Dreamcast "Sequencer File V1.0"
    ///     container, not a PSY-Q song — and a batch run should say so without reporting an
    ///     error it cannot act on.
    /// </summary>
    public bool Skipped { get; init; }
}
