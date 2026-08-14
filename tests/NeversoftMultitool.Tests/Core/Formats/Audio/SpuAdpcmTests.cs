using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class SpuAdpcmTests
{
    [Fact]
    public void Decode_NegativePredictorFeedback_UsesArithmeticShift()
    {
        byte[] block =
        [
            0x1C, SpuAdpcm.FlagEnd, 0x0F,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        ];

        var samples = SpuAdpcm.Decode(block);

        Assert.Equal(SpuAdpcm.SamplesPerBlock, samples.Length);
        Assert.All(samples, sample => Assert.Equal((short)-1, sample));
    }

    [Fact]
    public void Decode_PredictorTerms_AreShiftedIndependently()
    {
        byte[] block =
        [
            0x1C, SpuAdpcm.FlagEnd, 0x01,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        ];

        var samples = SpuAdpcm.Decode(block);

        Assert.Equal(SpuAdpcm.SamplesPerBlock, samples.Length);
        Assert.Equal((short)1, samples[0]);
        Assert.All(samples[1..], sample => Assert.Equal((short)0, sample));
    }

    [Fact]
    public void Decode_ReservedShiftNibble_UsesHardwareShiftNine()
    {
        byte[] block =
        [
            0x0D, SpuAdpcm.FlagEnd, 0x01,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        ];

        var samples = SpuAdpcm.Decode(block);

        Assert.Equal((short)8, samples[0]);
        Assert.All(samples[1..], sample => Assert.Equal((short)0, sample));
    }

    [Fact]
    public void CountDecodedSamples_StopsAfterFirstEndMarkedBlock()
    {
        var data = new byte[SpuAdpcm.BlockSize * 3];
        data[SpuAdpcm.BlockSize + 1] = SpuAdpcm.FlagEnd;

        var count = SpuAdpcm.CountDecodedSamples(data);

        Assert.Equal(SpuAdpcm.SamplesPerBlock * 2, count);
        Assert.Equal(count, SpuAdpcm.Decode(data).Length);
    }
}
