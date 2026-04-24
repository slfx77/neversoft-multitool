using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1DequantTests
{
    [Fact]
    public void DequantInter_DcScaling()
    {
        var input = new short[64];
        var output = new short[64];
        input[0] = 100;
        Vid1Dequant.DequantInter(output, input, 8, 3);
        Assert.Equal(300, output[0]); // 100 * 3
    }

    [Fact]
    public void DequantInter_AcPositive_OddQp()
    {
        var input = new short[64];
        var output = new short[64];
        input[1] = 5;
        Vid1Dequant.DequantInter(output, input, 7, 1); // qp=7 (odd): step=14, offset=7
        Assert.Equal(5 * 14 + 7, output[1]); // 77
    }

    [Fact]
    public void DequantInter_AcPositive_EvenQp()
    {
        var input = new short[64];
        var output = new short[64];
        input[1] = 5;
        Vid1Dequant.DequantInter(output, input, 8, 1); // qp=8 (even): step=16, offset=7
        Assert.Equal(5 * 16 + 7, output[1]); // 87
    }

    [Fact]
    public void DequantInter_AcNegative()
    {
        var input = new short[64];
        var output = new short[64];
        input[1] = -5;
        Vid1Dequant.DequantInter(output, input, 7, 1); // step=14, offset=7
        Assert.Equal(-(5 * 14 + 7), output[1]); // -77
    }

    [Fact]
    public void DequantInter_AcZero_StaysZero()
    {
        var input = new short[64];
        var output = new short[64];
        input[1] = 0;
        Vid1Dequant.DequantInter(output, input, 10, 1);
        Assert.Equal(0, output[1]);
    }

    [Fact]
    public void DequantInter_ClampsToRange()
    {
        var input = new short[64];
        var output = new short[64];
        input[0] = 2000;
        Vid1Dequant.DequantInter(output, input, 10, 3);
        Assert.Equal(2047, output[0]); // 2000*3 = 6000 → clamped to 2047
    }

    [Fact]
    public void DequantInter_AcNegative_ClampsToDolMinimum()
    {
        var input = new short[64];
        var output = new short[64];
        input[1] = -200;
        Vid1Dequant.DequantInter(output, input, 8, 1);
        Assert.Equal(-2048, output[1]);
    }

    [Fact]
    public void DequantIntra_DcScaling()
    {
        var input = new short[64];
        var output = new short[64];
        var matrix = new byte[64];
        input[0] = 50;
        Vid1Dequant.DequantIntra(output, input, 8, 5, matrix);
        Assert.Equal(250, output[0]); // 50 * 5
    }

    [Fact]
    public void DequantIntra_AcWithMatrix()
    {
        var input = new short[64];
        var output = new short[64];
        var matrix = new byte[64];
        matrix[1] = 16;
        input[1] = 10;
        Vid1Dequant.DequantIntra(output, input, 4, 1, matrix);
        // scale = matrix[1] * qp = 16 * 4 = 64
        // output = (10 * 64) >> 3 = 640 >> 3 = 80
        Assert.Equal(80, output[1]);
    }

    [Fact]
    public void DequantIntra_AcNegative_WithMatrix()
    {
        var input = new short[64];
        var output = new short[64];
        var matrix = new byte[64];
        matrix[1] = 16;
        input[1] = -10;
        Vid1Dequant.DequantIntra(output, input, 4, 1, matrix);
        Assert.Equal(-80, output[1]);
    }

    [Fact]
    public void DequantIntra_AcNegativeWithMatrix_ClampsToDolMinimum()
    {
        var input = new short[64];
        var output = new short[64];
        var matrix = new byte[64];
        matrix[1] = 255;
        input[1] = -200;
        Vid1Dequant.DequantIntra(output, input, 16, 1, matrix);
        Assert.Equal(-2048, output[1]);
    }

    [Fact]
    public void DequantInterResidual_Negative_ClampsToDolMinimum()
    {
        var input = new short[64];
        var output = new short[64];
        input[0] = -200;
        Vid1Dequant.DequantInterResidual(output, input, 8);
        Assert.Equal(-2048, output[0]);
    }

    [Fact]
    public void DequantInterResidualWithMatrix_Negative_ClampsToDolMinimum()
    {
        var input = new short[64];
        var output = new short[64];
        var matrix = new byte[64];
        matrix[0] = 255;
        input[0] = -200;
        Vid1Dequant.DequantInterResidual(output, input, 16, matrix);
        Assert.Equal(-2048, output[0]);
    }
}
