using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Vid1;

/// <summary>
///     Startup policies behind the .vid playback-start stutter fix: anchor
///     captures (a ~1 MB deep copy on the decode worker) must stay out of the
///     opening seconds, and the pipeline's initial Starting at position 0 must
///     not reposition away the pre-decoded queue — while real seeks (including
///     back to 0 after playing) always do.
/// </summary>
public sealed class Vid1StartupPolicyTests
{
    [Fact]
    public void ShouldCapture_BeforeFirstCaptureOrdinal_IsSuppressed()
    {
        var index = new Vid1SeekAnchorIndex();

        for (var ordinal = 1; ordinal < Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal; ordinal++)
            Assert.False(index.ShouldCapture(ordinal), $"unexpected capture at ordinal {ordinal}");

        Assert.True(index.ShouldCapture(Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal));
    }

    [Fact]
    public void ShouldCapture_AfterFirstAnchor_UsesStrideSpacing()
    {
        var index = new Vid1SeekAnchorIndex();
        index.Add(CreateAnchor(Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal));

        for (var offset = 1; offset < 30; offset++)
            Assert.False(
                index.ShouldCapture(Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal + offset),
                $"unexpected capture {offset} emissions after the first anchor");

        Assert.True(index.ShouldCapture(Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal + 30));
    }

    [Fact]
    public void FindBestAtOrBelow_BelowFirstCaptureOrdinal_HasNoAnchor()
    {
        var index = new Vid1SeekAnchorIndex();
        index.Add(CreateAnchor(Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal));

        Assert.Null(index.FindBestAtOrBelow(Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal - 1));
        Assert.NotNull(index.FindBestAtOrBelow(Vid1SeekAnchorIndex.FirstCaptureEmissionOrdinal));
    }

    [Theory]
    // Fresh start at 0 before anything decoded: keep the warm-up decode.
    [InlineData(0, false, -1, false, true)]
    // Fresh start at 0 with the pre-decoded queue already beginning at 0.
    [InlineData(0, false, 0, false, true)]
    // Later start at 0 while the (untouched) queue still begins at 0.
    [InlineData(0, false, 0, true, true)]
    // Seek back to 0 after playback consumed frames.
    [InlineData(0, false, 10, true, false)]
    [InlineData(0, false, 10, false, false)]
    // Start at 0 racing a reposition already executing on the worker
    // (pending flag consumed, queue drained) — must reposition again.
    [InlineData(0, false, -1, true, false)]
    // Start at 0 with an unconsumed seek still pending: the stale pending
    // target must be superseded, never silently kept.
    [InlineData(0, true, 0, true, false)]
    [InlineData(0, true, -1, false, false)]
    // Any nonzero start position always repositions.
    [InlineData(42, false, -1, false, false)]
    [InlineData(42, false, 0, true, false)]
    public void IsRedundantStartAtZero_MatchesContract(
        int targetPresentationIndex,
        bool repositionPending,
        int queueHeadPresentationIndex,
        bool startHandledBefore,
        bool expected)
    {
        Assert.Equal(
            expected,
            Vid1StartupSeekPolicy.IsRedundantStartAtZero(
                targetPresentationIndex,
                repositionPending,
                queueHeadPresentationIndex,
                startHandledBefore));
    }

    private static Vid1SeekAnchor CreateAnchor(int nextEmissionOrdinal)
    {
        return new Vid1SeekAnchor
        {
            DecodeIndex = nextEmissionOrdinal,
            EmittedInitialReference = true,
            HeldReferenceFrameIndex = -1,
            NextEmissionOrdinal = nextEmissionOrdinal,
            State = CreateEmptySnapshot()
        };
    }

    private static Vid1ReferenceSnapshot CreateEmptySnapshot()
    {
        return new Vid1ReferenceSnapshot
        {
            ReferenceY = [],
            ReferenceCb = [],
            ReferenceCr = [],
            PreviousReferenceY = [],
            PreviousReferenceCb = [],
            PreviousReferenceCr = [],
            ReferenceMbState = [],
            PreviousReferenceMbState = [],
            ReferenceStateWord = 0,
            PreviousReferenceStateWord = 0
        };
    }
}