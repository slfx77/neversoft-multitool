using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class SfxCueResolverTerminationTests
{
    private const string NonzeroAfterTerminatorError =
        "SFX cue table has nonzero data after its terminator";
    private const string NonzeroAfterZeroPaddingError =
        "SFX cue table has nonzero data after a zero-padding record";

    [Fact]
    public void TryParseCues_AlignedTerminatorRejectsNonzeroImmediatelyAfterFourByteSentinel()
    {
        var data = CreateCueTable(32);
        WriteTerminator(data, 16);
        data[20] = 0x7F;

        AssertRejected(data, NonzeroAfterTerminatorError);
    }

    [Fact]
    public void TryParseCues_AlignedTerminatorRejectsLaterNonzeroRecord()
    {
        var data = CreateCueTable(48);
        WriteTerminator(data, 16);
        WriteCue(data, 32, program: 2);

        AssertRejected(data, NonzeroAfterTerminatorError);
    }

    [Fact]
    public void TryParseCues_PartialTailTerminatorRejectsNonzeroSuffix()
    {
        var data = CreateCueTable(21);
        WriteTerminator(data, 16);
        data[20] = 0x7F;

        AssertRejected(data, NonzeroAfterTerminatorError);
    }

    [Fact]
    public void TryParseCues_FullZeroRecordRejectsLaterNonzeroRecord()
    {
        var data = CreateCueTable(48);
        WriteCue(data, 32, program: 2);

        AssertRejected(data, NonzeroAfterZeroPaddingError);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(47)]
    public void TryParseCues_TerminatorWithZeroOnlyTail_RemainsAccepted(int length)
    {
        var data = CreateCueTable(length);
        WriteTerminator(data, 16);

        AssertAccepted(data);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(39)]
    [InlineData(48)]
    public void TryParseCues_FullZeroRecordWithZeroOnlyTail_RemainsAccepted(int length)
    {
        AssertAccepted(CreateCueTable(length));
    }

    [Fact]
    public void TryParseCues_IncompleteAllZeroTail_RemainsAccepted()
    {
        AssertAccepted(CreateCueTable(17));
    }

    private static byte[] CreateCueTable(int length)
    {
        var data = new byte[length];
        WriteCue(data, 0, program: 1);
        return data;
    }

    private static void WriteCue(byte[] data, int offset, byte program)
    {
        data[offset + 1] = program;
        data[offset + 3] = 60;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 4), 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 6), 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 8), 0x00B0);
    }

    private static void WriteTerminator(byte[] data, int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), uint.MaxValue);
    }

    private static void AssertRejected(byte[] data, string expectedError)
    {
        var success = SfxCueResolver.TryParseCues(data, out var cues, out var error);

        Assert.False(success);
        Assert.Empty(cues);
        Assert.Equal(expectedError, error);
    }

    private static void AssertAccepted(byte[] data)
    {
        var success = SfxCueResolver.TryParseCues(data, out var cues, out var error);

        Assert.True(success, error);
        var cue = Assert.Single(cues);
        Assert.Equal(1, cue.Program);
        Assert.Equal("", error);
    }
}
