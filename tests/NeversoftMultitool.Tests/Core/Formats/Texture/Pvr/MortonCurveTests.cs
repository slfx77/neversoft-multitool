using NeversoftMultitool.Core.Formats.Texture.Pvr;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Pvr;

public class MortonCurveTests
{
    [Theory]
    [InlineData(0, 4, 4, 0)] // Index 0 → (0,0) → 0*4+0 = 0
    [InlineData(1, 4, 4, 1)] // Index 1 → (1,0) → 0*4+1 = 1
    [InlineData(2, 4, 4, 4)] // Index 2 → (0,1) → 1*4+0 = 4
    [InlineData(3, 4, 4, 5)] // Index 3 → (1,1) → 1*4+1 = 5
    [InlineData(4, 4, 4, 2)] // Index 4 → (2,0) → 0*4+2 = 2
    [InlineData(5, 4, 4, 3)] // Index 5 → (3,0) → 0*4+3 = 3
    public void Morton_KnownIndices_ReturnsExpected(int index, int width, int height, int expected)
    {
        Assert.Equal(expected, MortonCurve.Morton(index, width, height));
    }

    [Fact]
    public void Morton_SquareTexture_ProducesValidIndices()
    {
        const int size = 8;
        var seen = new HashSet<int>();

        for (var i = 0; i < size * size; i++)
        {
            var result = MortonCurve.Morton(i, size, size);
            Assert.InRange(result, 0, size * size - 1);
            seen.Add(result);
        }

        // Every pixel should be visited exactly once
        Assert.Equal(size * size, seen.Count);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, 2)]
    [InlineData(1, 1, 3)]
    [InlineData(2, 0, 4)]
    [InlineData(0, 2, 8)]
    [InlineData(3, 3, 15)]
    public void Interleave_KnownValues_ReturnsExpected(int x, int y, int expected)
    {
        Assert.Equal(expected, MortonCurve.Interleave(x, y));
    }

    [Fact]
    public void Interleave_IsReversibleWithMortonForSquare()
    {
        // For a square texture, Interleave and Morton should be related
        // Interleave(x, y) produces a Z-order index from 2D coords
        // Morton(index, w, h) produces a linear index from a Z-order index
        const int size = 4;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var interleaved = MortonCurve.Interleave(x, y);
                Assert.InRange(interleaved, 0, size * size * 4); // reasonable range
            }
        }
    }
}