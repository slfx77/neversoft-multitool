namespace NeversoftMultitool.Core.Formats.Vid1;

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
