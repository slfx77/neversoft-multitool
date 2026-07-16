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

/// <summary>
///     Bounded set of seek anchors built as playback/seeks advance. Anchors are
///     spaced <see cref="_stride" /> emissions apart; when the cap is reached,
///     every other anchor is dropped and the stride doubles, so memory stays
///     bounded (~1 MB per anchor) on arbitrarily long streams while backward
///     seeks stay O(stride) decodes instead of O(position).
/// </summary>
internal sealed class Vid1SeekAnchorIndex
{
    /// <summary>
    ///     No anchor is captured before this emission ordinal (~3 s at
    ///     29.97 fps). The first <c>CaptureReferenceState</c> deep copy (~1 MB)
    ///     otherwise lands on the decode worker about a second into playback,
    ///     while the startup queue is still building headroom — deferring it
    ///     keeps the opening seconds stutter-free. Anchors are opportunistic
    ///     (seeks below the first anchor fall back to the nearest intra frame
    ///     or a stream restart), so the only cost is slightly slower very-early
    ///     backward seeks.
    /// </summary>
    internal const int FirstCaptureEmissionOrdinal = 90;

    private const int MaxAnchors = 40;
    private readonly List<Vid1SeekAnchor> _anchors = [];
    private int _stride = 30;

    /// <summary>Anchors are only captured past the current high-water mark.</summary>
    public bool ShouldCapture(int nextEmissionOrdinal)
    {
        return _anchors.Count == 0
            ? nextEmissionOrdinal >= FirstCaptureEmissionOrdinal
            : nextEmissionOrdinal - _anchors[^1].NextEmissionOrdinal >= _stride;
    }

    public void Add(Vid1SeekAnchor anchor)
    {
        _anchors.Add(anchor);
        if (_anchors.Count < MaxAnchors)
            return;

        // Thin out: keep every other anchor, double the spacing.
        for (var i = _anchors.Count - 1; i >= 0; i -= 2)
            _anchors.RemoveAt(i);
        _stride *= 2;
    }

    /// <summary>Best anchor whose resume point is at or before the target ordinal.</summary>
    public Vid1SeekAnchor? FindBestAtOrBelow(int targetEmissionOrdinal)
    {
        Vid1SeekAnchor? best = null;
        foreach (var anchor in _anchors)
        {
            if (anchor.NextEmissionOrdinal > targetEmissionOrdinal)
                break;

            best = anchor;
        }

        return best;
    }
}

/// <summary>
///     Playback-start policy for <c>Vid1MediaSource</c> (kept here so the WinRT
///     class stays thin and the decision is unit-testable without WinUI). The
///     decode worker pre-decodes from frame 0 the moment the source is created,
///     so the pipeline's initial Starting request at position 0 is already
///     satisfied — repositioning for it would recycle the warm queue and
///     re-decode from frame 0, the startup stutter. A start is redundant only
///     when it targets ordinal 0 AND the queued frames genuinely begin at
///     presentation index 0 (or nothing has been decoded yet on the very first
///     start); resuming mid-stream or re-seeking to 0 after playback must still
///     reposition.
/// </summary>
internal static class Vid1StartupSeekPolicy
{
    /// <summary>Whether a Starting request can keep the current decode position and queue untouched.</summary>
    /// <param name="targetPresentationIndex">Presentation ordinal the Starting request resolves to.</param>
    /// <param name="repositionPending">Whether an unconsumed seek is already queued for the worker (its target, not the queue contents, defines the decode position).</param>
    /// <param name="queueHeadPresentationIndex">Presentation ordinal of the oldest queued frame, or -1 when the queue is empty.</param>
    /// <param name="startHandledBefore">Whether any Starting request was handled before this one.</param>
    public static bool IsRedundantStartAtZero(
        int targetPresentationIndex,
        bool repositionPending,
        int queueHeadPresentationIndex,
        bool startHandledBefore)
    {
        if (targetPresentationIndex != 0 || repositionPending)
            return false;

        // Non-empty queue: trust its contents. Empty queue: only the very
        // first start is guaranteed to be decoding from frame 0 (a later
        // Starting can race a reposition already executing on the worker).
        return queueHeadPresentationIndex >= 0
            ? queueHeadPresentationIndex == 0
            : !startHandledBefore;
    }
}
