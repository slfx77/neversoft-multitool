namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     A resumable decode position: restore the reference snapshot, rewind the
///     provider bookkeeping to these values, and decoding continues exactly as
///     it originally did from this point.
/// </summary>
internal sealed class Vid1SeekAnchor
{
    /// <summary>Container index of the next frame the provider will decode.</summary>
    public required int DecodeIndex { get; init; }

    public required bool EmittedInitialReference { get; init; }

    public required int HeldReferenceFrameIndex { get; init; }

    /// <summary>Presentation ordinal of the next frame the provider will emit.</summary>
    public required int NextEmissionOrdinal { get; init; }

    public required Vid1ReferenceSnapshot State { get; init; }
}
