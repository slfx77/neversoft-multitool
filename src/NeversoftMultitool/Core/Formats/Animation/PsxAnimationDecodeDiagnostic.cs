namespace NeversoftMultitool.Core.Formats.Animation;

internal sealed record PsxAnimationDecodeDiagnostic(
    int Index,
    string Name,
    int FrameCount,
    int? BytesConsumed,
    string? Error)
{
    public bool Succeeded => Error == null;
}
