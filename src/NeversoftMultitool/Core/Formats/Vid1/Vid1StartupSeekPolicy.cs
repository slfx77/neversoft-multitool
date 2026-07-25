namespace NeversoftMultitool.Core.Formats.Vid1;

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
    /// <param name="repositionPending">
    ///     Whether an unconsumed seek is already queued for the worker (its target, not the queue
    ///     contents, defines the decode position).
    /// </param>
    /// <param name="queueHeadPresentationIndex">
    ///     Presentation ordinal of the oldest queued frame, or -1 when the queue is
    ///     empty.
    /// </param>
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
