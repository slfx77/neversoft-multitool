using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class XboxImaAdpcmValidationTests
{
    [Fact]
    public void Decode_InitialStepIndexAboveTableRange_ThrowsInvalidDataException()
    {
        var data = new byte[XboxImaAdpcm.BlockAlignPerChannel];
        data[2] = 89;

        var exception = Assert.Throws<InvalidDataException>(() => XboxImaAdpcm.Decode(data, 1));

        Assert.Equal(
            "Xbox ADPCM block 0, channel 0 has invalid initial step index 89 (expected 0..88)",
            exception.Message);
    }

    [Fact]
    public void Decode_NonzeroReservedHeaderByte_ThrowsInvalidDataException()
    {
        var data = new byte[XboxImaAdpcm.BlockAlignPerChannel];
        data[3] = 1;

        var exception = Assert.Throws<InvalidDataException>(() => XboxImaAdpcm.Decode(data, 1));

        Assert.Equal(
            "Xbox ADPCM block 0, channel 0 has nonzero reserved byte 0x01",
            exception.Message);
    }

    [Fact]
    public void Decode_InvalidSecondChannelInLaterBlock_ReportsItsLocation()
    {
        const int channels = 2;
        var blockAlign = XboxImaAdpcm.BlockAlignPerChannel * channels;
        var data = new byte[blockAlign * 2];
        data[blockAlign + XboxImaAdpcm.BlockAlignPerChannel + 2] = 89;

        var exception = Assert.Throws<InvalidDataException>(() => XboxImaAdpcm.Decode(data, channels));

        Assert.Equal(
            "Xbox ADPCM block 1, channel 1 has invalid initial step index 89 (expected 0..88)",
            exception.Message);
    }

    [Fact]
    public void Decode_ValidZeroBlock_ProducesSixtyFourSamples()
    {
        var data = new byte[XboxImaAdpcm.BlockAlignPerChannel];

        var samples = XboxImaAdpcm.Decode(data, 1);

        Assert.Equal(XboxImaAdpcm.SamplesPerBlock, samples.Length);
        Assert.All(samples, static sample => Assert.Equal((short)0, sample));
    }

    [Fact]
    public void Decode_InvalidHeaderBytesInTrailingPartialBlock_AreIgnored()
    {
        var data = new byte[XboxImaAdpcm.BlockAlignPerChannel + 4];
        data[XboxImaAdpcm.BlockAlignPerChannel + 2] = byte.MaxValue;
        data[XboxImaAdpcm.BlockAlignPerChannel + 3] = byte.MaxValue;

        var samples = XboxImaAdpcm.Decode(data, 1);

        Assert.Equal(XboxImaAdpcm.SamplesPerBlock, samples.Length);
    }
}
