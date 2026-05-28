using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1MotionCompTests
{
    [Fact]
    public void PredictInterBlock_FullPel_ZeroResidual_CopiesReference()
    {
        var reference = MakeGradient(16, 16);
        var residual = new short[64];
        var output = new byte[16 * 16];

        Vid1MotionComp.PredictInterBlock(
            reference, 16, 16, 16,
            srcX: 4, srcY: 4, halfX: 0, halfY: 0,
            residual,
            output, 16,
            dstX: 0, dstY: 0);

        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                Assert.Equal(reference[((4 + y) * 16) + (4 + x)], output[(y * 16) + x]);
    }

    [Fact]
    public void PredictInterBlock_HalfPelX_AveragesHorizontalNeighbors()
    {
        var reference = new byte[16 * 16];
        for (var i = 0; i < reference.Length; i++) reference[i] = 100;
        // Set a distinctive pair at (4,4) and (5,4)
        reference[(4 * 16) + 4] = 100;
        reference[(4 * 16) + 5] = 150;

        var residual = new short[64];
        var output = new byte[16 * 16];

        Vid1MotionComp.PredictInterBlock(
            reference, 16, 16, 16,
            srcX: 4, srcY: 4, halfX: 1, halfY: 0,
            residual,
            output, 16,
            dstX: 0, dstY: 0);

        // Output[0,0] = (100 + 150 + 1) >> 1 = 125
        Assert.Equal(125, output[0]);
    }

    [Fact]
    public void PredictInterBlock_HalfPelY_AveragesVerticalNeighbors()
    {
        var reference = new byte[16 * 16];
        for (var i = 0; i < reference.Length; i++) reference[i] = 100;
        reference[(4 * 16) + 4] = 100;
        reference[(5 * 16) + 4] = 150;

        var residual = new short[64];
        var output = new byte[16 * 16];

        Vid1MotionComp.PredictInterBlock(
            reference, 16, 16, 16,
            srcX: 4, srcY: 4, halfX: 0, halfY: 1,
            residual,
            output, 16,
            dstX: 0, dstY: 0);

        Assert.Equal(125, output[0]);
    }

    [Fact]
    public void PredictInterBlock_HalfPelBoth_AveragesFour()
    {
        var reference = new byte[16 * 16];
        for (var i = 0; i < reference.Length; i++) reference[i] = 200;
        reference[(4 * 16) + 4] = 100; // a
        reference[(4 * 16) + 5] = 120; // b
        reference[(5 * 16) + 4] = 140; // c
        reference[(5 * 16) + 5] = 160; // d
        var residual = new short[64];
        var output = new byte[16 * 16];

        Vid1MotionComp.PredictInterBlock(
            reference, 16, 16, 16,
            srcX: 4, srcY: 4, halfX: 1, halfY: 1,
            residual,
            output, 16,
            dstX: 0, dstY: 0);

        // Output[0,0] = (100 + 120 + 140 + 160 + 2) >> 2 = 130
        Assert.Equal(130, output[0]);
    }

    [Fact]
    public void PredictInterBlock_ResidualAdded_ClampsTop()
    {
        var reference = new byte[16 * 16];
        for (var i = 0; i < reference.Length; i++) reference[i] = 200;
        var residual = new short[64];
        residual[0] = 200; // predicted(200) + 200 = 400 → clamped to 255
        var output = new byte[16 * 16];

        Vid1MotionComp.PredictInterBlock(
            reference, 16, 16, 16,
            srcX: 0, srcY: 0, halfX: 0, halfY: 0,
            residual,
            output, 16,
            dstX: 0, dstY: 0);

        Assert.Equal(255, output[0]);
    }

    [Fact]
    public void PredictInterBlock_ResidualAdded_ClampsBottom()
    {
        var reference = new byte[16 * 16];
        for (var i = 0; i < reference.Length; i++) reference[i] = 50;
        var residual = new short[64];
        residual[0] = -100; // 50 - 100 = -50 → clamped to 0
        var output = new byte[16 * 16];

        Vid1MotionComp.PredictInterBlock(
            reference, 16, 16, 16,
            srcX: 0, srcY: 0, halfX: 0, halfY: 0,
            residual,
            output, 16,
            dstX: 0, dstY: 0);

        Assert.Equal(0, output[0]);
    }

    [Fact]
    public void PredictInterBlock_OutOfBoundsSrc_EdgePads()
    {
        var reference = new byte[16 * 16];
        // Set edge value
        for (var y = 0; y < 16; y++)
            reference[y * 16] = 77;
        var residual = new short[64];
        var output = new byte[16 * 16];

        // Src at (-5, 0) should clamp to column 0, returning the 77s
        Vid1MotionComp.PredictInterBlock(
            reference, 16, 16, 16,
            srcX: -5, srcY: 0, halfX: 0, halfY: 0,
            residual,
            output, 16,
            dstX: 0, dstY: 0);

        for (var y = 0; y < 8; y++)
            Assert.Equal(77, output[y * 16]);
    }

    [Fact]
    public void WriteIntraBlock_AddsDcOffset()
    {
        var samples = new short[64];
        // Zero IDCT output + 128 DC offset → 128 all around
        var output = new byte[16 * 16];

        Vid1MotionComp.WriteIntraBlock(samples, output, 16, 0, 0);

        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                Assert.Equal(128, output[(y * 16) + x]);
    }

    [Fact]
    public void WriteIntraBlock_ClampsOverflow()
    {
        var samples = new short[64];
        samples[0] = 200; // 200 + 128 = 328 → clamp 255
        samples[1] = -200; // -200 + 128 = -72 → clamp 0
        var output = new byte[16 * 16];

        Vid1MotionComp.WriteIntraBlock(samples, output, 16, 0, 0);

        Assert.Equal(255, output[0]);
        Assert.Equal(0, output[1]);
    }

    [Fact]
    public void CopyReferenceBlock_PreservesExactBytes()
    {
        var reference = MakeGradient(16, 16);
        var output = new byte[16 * 16];

        Vid1MotionComp.CopyReferenceBlock(reference, 16, output, 16, 2, 3);

        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                Assert.Equal(reference[((3 + y) * 16) + (2 + x)], output[((3 + y) * 16) + (2 + x)]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(13, 1)]
    [InlineData(14, 2)]
    [InlineData(15, 2)]
    [InlineData(16, 2)]
    [InlineData(17, 2)]
    [InlineData(30, 4)]
    [InlineData(-3, -1)]
    [InlineData(-14, -2)]
    [InlineData(-30, -4)]
    public void RoundFourMotionChroma_MatchesDolTable(int sum, int expected)
        => Assert.Equal(expected, Vid1MacroblockDecoder.RoundFourMotionChroma(sum));

    [Fact]
    public void ComputeSpriteBlockSource_UsesAccuracyShiftAndFourBitFraction()
    {
        var source = Vid1MacroblockDecoder.ComputeSpriteBlockSource(
            baseX: 32,
            baseY: 48,
            offsetX: 31,
            offsetY: -17,
            accuracy: 2,
            minCoord: -16,
            maxX: 128,
            maxY: 128);

        Assert.Equal((35, 45, 14, 14), source);
    }

    [Fact]
    public void ComputeSpriteBlockSource_HighClipClearsFractionButLowClipKeepsIt()
    {
        var high = Vid1MacroblockDecoder.ComputeSpriteBlockSource(
            baseX: 80,
            baseY: 80,
            offsetX: 7,
            offsetY: 7,
            accuracy: 1,
            minCoord: -16,
            maxX: 64,
            maxY: 64);
        var low = Vid1MacroblockDecoder.ComputeSpriteBlockSource(
            baseX: -40,
            baseY: -40,
            offsetX: 5,
            offsetY: 5,
            accuracy: 1,
            minCoord: -16,
            maxX: 64,
            maxY: 64);

        Assert.Equal((64, 64, 0, 0), high);
        Assert.Equal((-16, -16, 4, 4), low);
    }

    [Fact]
    public void PredictFieldBlock_UsesSeparateFieldVectorsAndSelectBits()
    {
        var reference = MakeGradient(16, 16);
        var output = new byte[16 * 16];

        Vid1MotionComp.PredictFieldBlock(
            reference, 16, 16, 16,
            output, 16,
            dstX: 0, dstY: 0,
            blockWidth: 4,
            blockHeight: 4,
            firstMvX: 0,
            firstMvY: 0,
            secondMvX: 2,
            secondMvY: 0,
            fieldSelectBits: 0b10);

        Assert.Equal(reference[0], output[0]);
        Assert.Equal(reference[(1 * 16) + 1], output[16]);
        Assert.Equal(reference[2 * 16], output[2 * 16]);
        Assert.Equal(reference[(3 * 16) + 1], output[3 * 16]);
    }

    private static byte[] MakeGradient(int width, int height)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                result[(y * width) + x] = (byte)((x * 7 + y * 11) & 0xFF);
        return result;
    }
}
