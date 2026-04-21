using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class Vid1VideoRebuilderTests
{
    [Fact]
    public void GetDeterministicFramePlan_LongFormSpecial4014_ReturnsExpectedPlan()
    {
        var frame = new Vid1VideoFrame(0, 0x4014, 0, false, false, [0x80, 0x01], [], 0, 7, null, null, 0x11223344, null, true, null, null);

        var plan = Vid1VideoRebuilder.GetDeterministicFramePlan(Vid1VideoVariant.ThawLongForm, frame);

        Assert.NotNull(plan);
        Assert.Equal(56, plan!.Value.PayloadOffsetBytes);
        Assert.Equal(0, plan.Value.VopType);
    }

    [Theory]
    [InlineData(0x5014)]
    [InlineData(0x5044)]
    public void GetDeterministicFramePlan_LongFormSpecial5014Family_ReturnsExpectedPlan(ushort tag16)
    {
        var frame = new Vid1VideoFrame(0, tag16, 1, false, false, [0x80, 0x01], [], 0, 8, 3, null, 0x11223344, null, true, null, null);

        var plan = Vid1VideoRebuilder.GetDeterministicFramePlan(Vid1VideoVariant.ThawLongForm, frame);

        Assert.NotNull(plan);
        Assert.Equal(580, plan!.Value.PayloadOffsetBytes);
        Assert.Equal(1, plan.Value.VopType);
    }

    [Fact]
    public void GetDeterministicFramePlan_AtviSpecial_ReturnsExpectedPlan()
    {
        var frame = new Vid1VideoFrame(0, 0x8046, 0, false, false, [0x80, 0x01], [], 0, 9, null, null, 0x55667788, null, true, null, null);

        var plan = Vid1VideoRebuilder.GetDeterministicFramePlan(Vid1VideoVariant.ThawAtvi, frame);

        Assert.NotNull(plan);
        Assert.Equal(500, plan!.Value.PayloadOffsetBytes);
        Assert.Equal(0, plan.Value.VopType);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void GetDeterministicFramePlan_FallbackUsesPreambleClassMapping(int preambleClass, int expectedVopType)
    {
        var frame = new Vid1VideoFrame(0, 0x2002, preambleClass, false, false, [0x01, 0x02], [], 0, 6, 2, 1, 0x11223344, null, false, null, null);

        var plan = Vid1VideoRebuilder.GetDeterministicFramePlan(Vid1VideoVariant.Unknown, frame);

        Assert.NotNull(plan);
        Assert.Equal(0, plan!.Value.PayloadOffsetBytes);
        Assert.Equal(expectedVopType, plan.Value.VopType);
    }

    [Fact]
    public void BuildDeterministicCandidateStream_BeginsWithPrefixAndSkipsIneligibleFrames()
    {
        var prefix = new byte[] { 0x00, 0x00, 0x01, 0xB0, 0x20 };
        var payload = Enumerable.Range(0, 96).Select(static value => (byte)value).ToArray();
        var data = Vid1VideoTestBuilder.CreateVideoVid1(
            frames:
            [
                new Vid1SyntheticVideoFrameSpec(
                    0x2107,
                    PreambleClass: 0,
                    Quantizer: 7,
                    CurrentFrameStateWord: 0x11223344,
                    HasSpecialCallerGate: true,
                    CodedPayload: payload),
                new Vid1SyntheticVideoFrameSpec(
                    0x2107,
                    PreambleClass: 0,
                    Quantizer: 7,
                    CurrentFrameStateWord: 0x11223344,
                    HasSpecialCallerGate: true,
                    CodedPayload: Enumerable.Repeat((byte)0xEE, 24).ToArray()),
                new Vid1SyntheticVideoFrameSpec(
                    0x6009,
                    PreambleClass: 1,
                    Quantizer: 9,
                    ForwardCode: 3,
                    CurrentFrameStateWord: 0x55667788,
                    CodedPayload: [0x21, 0x43, 0x65, 0x87])
            ]);

        var success = Vid1VideoFile.TryParse(data, "intro.vid", out var file, out var error);
        Assert.True(success, error);

        var rebuilt = Vid1VideoRebuilder.BuildDeterministicCandidateStream(prefix, file!);
        var expectedLongFormTail = payload.AsSpan(56).ToArray();

        Assert.True(rebuilt.AsSpan(0, prefix.Length).SequenceEqual(prefix));
        Assert.True(ContainsSubsequence(rebuilt, expectedLongFormTail));
        Assert.True(ContainsSubsequence(rebuilt, [0x21, 0x43, 0x65, 0x87]));
        Assert.False(ContainsSubsequence(rebuilt, Enumerable.Repeat((byte)0xEE, 24).ToArray()));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        }

        return false;
    }
}
