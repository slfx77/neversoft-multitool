using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1IdctTests
{
    [Fact]
    public void Transform_AllZeros_StaysZero()
    {
        var block = new short[64];
        Vid1Idct.Transform(block);

        for (var i = 0; i < 64; i++)
            Assert.Equal(0, block[i]);
    }

    [Fact]
    public void Transform_DcOnly_UniformOutput()
    {
        var block = new short[64];
        block[0] = 128;

        Vid1Idct.Transform(block);

        var expected = block[0];
        for (var i = 0; i < 64; i++)
            Assert.Equal(expected, block[i]);
    }

    [Fact]
    public void Transform_DcOnly_CorrectValue()
    {
        var block = new short[64];
        block[0] = 800;

        Vid1Idct.Transform(block);

        // DC-only IDCT: each output = DC * C4 * C4 * 64 normalization
        // With our constants: C4 = cos(4π/16)/2 = 1/(2√2).
        // row pass: DC * C4 → stored as float. column pass: that * C4 → rounded.
        // row: 800 * 0.35355339 = 282.8427...
        // col: 282.8427 * 0.35355339 = 100.000... + 0.5 = 100
        var expected = (short)(800.0 * 0.35355339059327373 * 0.35355339059327373 + 0.5);
        Assert.Equal(expected, block[0]);
    }

    [Fact]
    public void Transform_SingleAcCoefficient_ProducesNonUniformOutput()
    {
        var block = new short[64];
        block[1] = 100; // AC(0,1)

        Vid1Idct.Transform(block);

        // With a single non-DC coefficient, output should vary across positions
        Assert.NotEqual(block[0], block[4]);
    }

    [Fact]
    public void Transform_SingleVerticalFrequencyCoefficient_MatchesDolColumnPass()
    {
        var block = new short[64];
        block[16] = 256; // AC(2,0)

        Vid1Idct.Transform(block);

        short[] expectedFirstColumn = [42, 17, -17, -42, -42, -17, 17, 42];
        for (var y = 0; y < 8; y++)
            Assert.Equal(expectedFirstColumn[y], block[y * 8]);
    }

    [Fact]
    public void Transform_RoundTrip_DcPreserved()
    {
        // Verify the DC coefficient value is plausible after transform
        var block = new short[64];
        block[0] = 1000;

        Vid1Idct.Transform(block);

        // Output should be uniform and positive for positive DC
        Assert.True(block[0] > 0, "DC-only transform should produce positive output");
        Assert.True(block[0] < 1000, "Output should be smaller than input DC (normalization)");
    }
}
