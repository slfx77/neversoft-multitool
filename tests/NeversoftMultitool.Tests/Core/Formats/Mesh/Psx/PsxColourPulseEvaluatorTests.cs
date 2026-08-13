using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public class PsxColourPulseEvaluatorTests
{
    private static PsxColourPulseKey[] RgbCycle(byte interval = 32)
    {
        return
        [
            new PsxColourPulseKey(255, 0, 0, interval),
            new PsxColourPulseKey(0, 255, 0, interval),
            new PsxColourPulseKey(0, 0, 255, interval)
        ];
    }

    [Fact]
    public void Evaluate_AtTheSerializedPlayhead_ReturnsTheFirstKey()
    {
        var colour = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 0);

        Assert.Equal(255f, colour.X);
        Assert.Equal(0f, colour.Y);
    }

    [Fact]
    public void Evaluate_MidInterval_LerpsTowardTheNextKey()
    {
        // Half of the 32-frame interval: exactly between red and green.
        var colour = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 16);

        Assert.Equal(127.5f, colour.X, 3);
        Assert.Equal(127.5f, colour.Y, 3);
        Assert.Equal(0f, colour.Z, 3);
    }

    [Fact]
    public void Evaluate_AccumulatorPastSeveralIntervals_WalksToTheRightKey()
    {
        // 64 frames = two whole intervals, landing exactly on the third key.
        var colour = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 64);

        Assert.Equal(0f, colour.X);
        Assert.Equal(0f, colour.Y);
        Assert.Equal(255f, colour.Z);
    }

    [Fact]
    public void Evaluate_WrapsPastTheEndOfTheKeyList()
    {
        // One full cycle (96) returns to the start.
        var start = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 0);
        var wrapped = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 0, 96);

        Assert.Equal(start, wrapped);
    }

    /// <summary>
    ///     The playhead is periodic in the cycle length, so any frame must agree
    ///     with its wrapped equivalent no matter how far into playback it is.
    ///     Walking one interval per iteration could not reach a far-future frame
    ///     under the evaluator's guard: the walk stopped early with time still
    ///     past the interval, the blend clamped to 1, and the pulse froze on that
    ///     key for the rest of playback. With a 32-frame interval that happened
    ///     about 8,192 frames (~2 minutes at 60 Hz) in.
    /// </summary>
    [Theory]
    [InlineData(8_192)]
    [InlineData(8_208)]
    [InlineData(100_016)]
    [InlineData(1_000_003)]
    public void Evaluate_FarIntoPlayback_AgreesWithTheWrappedFrame(int frameOffset)
    {
        var cycle = PsxColourPulseEvaluator.CycleFrames(RgbCycle());

        var late = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 0, frameOffset);
        var wrapped = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 0, frameOffset % cycle);

        Assert.Equal(wrapped, late);
    }

    /// <summary>Short intervals reached the old guard sooner — cycle 6, not 96.</summary>
    [Fact]
    public void Evaluate_ShortIntervalsFarIntoPlayback_StillAgreeWithTheWrappedFrame()
    {
        var keys = RgbCycle(2);
        var cycle = PsxColourPulseEvaluator.CycleFrames(keys);

        for (var offset = 10_000; offset < 10_000 + cycle; offset++)
        {
            Assert.Equal(
                PsxColourPulseEvaluator.Evaluate(keys, 0, 0, offset % cycle),
                PsxColourPulseEvaluator.Evaluate(keys, 0, 0, offset));
        }
    }

    /// <summary>
    ///     The freeze presented as "pulses eventually stop", so assert motion
    ///     directly: a run of consecutive late frames must not all be identical.
    /// </summary>
    [Fact]
    public void Evaluate_LateFrames_StillAnimate()
    {
        var first = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 0, 500_000);
        var moved = false;
        for (var offset = 500_001; offset < 500_000 + 96; offset++)
        {
            if (PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 0, offset) != first)
            {
                moved = true;
                break;
            }
        }

        Assert.True(moved, "colour pulse stopped animating late in playback");
    }

    /// <summary>A serialized accumulator past one cycle must not shift the phase.</summary>
    [Fact]
    public void Evaluate_AccumulatorPastAWholeCycle_MatchesTheWrappedAccumulator()
    {
        // 200 accumulator frames over a 96-frame cycle wraps to 8.
        var wrapped = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 8);
        var raw = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 0, 200);

        Assert.Equal(wrapped, raw);
    }

    [Fact]
    public void Evaluate_ZeroInterval_HoldsItsKey()
    {
        PsxColourPulseKey[] keys = [new(10, 20, 30, 0), new(200, 200, 200, 0)];

        var colour = PsxColourPulseEvaluator.Evaluate(keys, 0, 0, 1000);

        Assert.Equal(10f, colour.X);
        Assert.Equal(20f, colour.Y);
        Assert.Equal(30f, colour.Z);
    }

    [Fact]
    public void Evaluate_KeyIndexBeyondTheList_FallsBackToZero()
    {
        var colour = PsxColourPulseEvaluator.Evaluate(RgbCycle(), 99, 0);

        Assert.Equal(255f, colour.X);
    }

    [Fact]
    public void Evaluate_EmptyKeys_ReturnsZero()
    {
        Assert.Equal(System.Numerics.Vector3.Zero, PsxColourPulseEvaluator.Evaluate([], 0, 0));
    }

    [Fact]
    public void CycleFrames_SumsEveryInterval()
    {
        Assert.Equal(96, PsxColourPulseEvaluator.CycleFrames(RgbCycle()));
        Assert.Equal(0, PsxColourPulseEvaluator.CycleFrames([new PsxColourPulseKey(1, 2, 3, 0)]));
    }

    /// <summary>
    ///     COLOR_1 alpha is an exact byte code: zero is static and 1..255 are
    ///     1-based channel identifiers.
    /// </summary>
    [Theory]
    [InlineData(1, 1f / 255f, 0)]
    [InlineData(2, 2f / 255f, 1)]
    [InlineData(6, 6f / 255f, 5)]
    public void Lane_RoundTripsAChannel(int oneBasedChannel, float expectedLane, int expectedIndex)
    {
        var lane = PsxColourPulseLane.Encode(oneBasedChannel);

        Assert.Equal(expectedLane, lane);
        Assert.Equal(expectedIndex, PsxColourPulseLane.DecodeIndex(lane));
    }

    [Fact]
    public void Lane_ZeroDecodesToNoChannel()
    {
        Assert.Equal(-1, PsxColourPulseLane.DecodeIndex(0f));
    }

    [Fact]
    public void Lane_RejectsValuesOutsideTheByteCodebook()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PsxColourPulseLane.Encode(256));
    }
}
