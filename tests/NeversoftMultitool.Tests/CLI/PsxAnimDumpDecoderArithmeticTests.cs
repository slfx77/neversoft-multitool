using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxAnimDumpDecoderArithmeticTests
{
    [Fact]
    public void CalculateTranslationSpanLength_FullSignedShortRange_DoesNotOverflow()
    {
        var length = PsxAnimDumpDecoder.CalculateTranslationSpanLength(
            ushort.MaxValue,
            0,
            0);

        Assert.True(float.IsFinite(length));
        Assert.Equal(65535f, length);
    }

    [Fact]
    public void CalculateTranslationSpanLength_OrdinaryVector_ReturnsEuclideanLength()
    {
        Assert.Equal(
            13f,
            PsxAnimDumpDecoder.CalculateTranslationSpanLength(3, 4, 12));
    }
}
