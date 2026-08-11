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
