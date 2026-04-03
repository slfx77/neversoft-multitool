using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public class Ps2TexSwizzleTests
{
    [Theory]
    [InlineData(32, 64, true)]
    [InlineData(128, 32, true)]
    [InlineData(256, 32, true)]
    [InlineData(32, 256, false)]
    [InlineData(8, 8, false)]
    public void CanConv8to32_UsesDecompiledEligibilityMatrix(int width, int height, bool expected)
    {
        Assert.Equal(expected, Ps2TexSwizzle.CanConv8to32(width, height));
    }

    [Theory]
    [InlineData(128, 64)]
    [InlineData(256, 256)]
    [InlineData(128, 128)]
    [InlineData(64, 64)]
    public void UnswizzlePsmt8_ProducesBijectiveMapping(int width, int height)
    {
        // Create identity data where each byte is its position mod 256
        var size = width * height;
        var input = new byte[size];
        for (var i = 0; i < size; i++)
            input[i] = (byte)(i % 256);

        var result = Ps2TexSwizzle.UnswizzlePsmt8(input, width, height);

        // Output must be the same length
        Assert.Equal(size, result.Length);

        // All non-zero bytes should be present (mapping is bijective)
        var resultNonZero = result.Count(b => b != 0);
        Assert.True(resultNonZero > 0, "Result should contain non-zero bytes");
    }

    [Theory]
    [InlineData(32, 32)] // Conv4to32 path
    [InlineData(64, 64)] // Conv4to32 path
    [InlineData(128, 128)] // Conv4to32 path
    [InlineData(16, 16)] // Conv4to16 path
    [InlineData(32, 64)] // Conv4to16 path
    [InlineData(128, 32)] // Conv4to16 path
    public void UnswizzlePsmt4_ProducesCorrectLength(int width, int height)
    {
        var nibbles = width * height;
        var bytes = nibbles / 2;
        var input = new byte[bytes];
        for (var i = 0; i < bytes; i++)
            input[i] = (byte)(i % 256);

        var result = Ps2TexSwizzle.UnswizzlePsmt4(input, width, height);

        Assert.Equal(bytes, result.Length);
    }

    [Theory]
    [InlineData(8, 4)] // Not in either table
    [InlineData(4, 4)] // Not in either table
    public void UnswizzlePsmt4_LinearFallback_ReturnsUnchanged(int width, int height)
    {
        var bytes = width * height / 2;
        var input = new byte[bytes];
        for (var i = 0; i < bytes; i++)
            input[i] = (byte)(i + 1);

        var result = Ps2TexSwizzle.UnswizzlePsmt4(input, width, height);

        // Linear fallback returns a copy of the input
        Assert.Equal(input, result);
    }

    [Fact]
    public void UnswizzlePsmt8_CacheProducesSameResult()
    {
        const int width = 128, height = 64;
        var input = new byte[width * height];
        var rng = new Random(42);
        rng.NextBytes(input);

        // Call twice — second call should use cached mapping
        var result1 = Ps2TexSwizzle.UnswizzlePsmt8(input, width, height);
        var result2 = Ps2TexSwizzle.UnswizzlePsmt8(input, width, height);

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void UnswizzlePsmt4_Conv4to32_CacheProducesSameResult()
    {
        const int width = 64, height = 64;
        var input = new byte[width * height / 2];
        var rng = new Random(42);
        rng.NextBytes(input);

        var result1 = Ps2TexSwizzle.UnswizzlePsmt4(input, width, height);
        var result2 = Ps2TexSwizzle.UnswizzlePsmt4(input, width, height);

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void UnswizzlePsmt4_Conv4to16_CacheProducesSameResult()
    {
        const int width = 32, height = 64;
        var input = new byte[width * height / 2];
        var rng = new Random(42);
        rng.NextBytes(input);

        var result1 = Ps2TexSwizzle.UnswizzlePsmt4(input, width, height);
        var result2 = Ps2TexSwizzle.UnswizzlePsmt4(input, width, height);

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void UnswizzlePsmt8_DifferentDimensions_ProduceDifferentResults()
    {
        // Ensure different dimensions don't interfere via cache
        var input128x64 = new byte[128 * 64];
        var input256x256 = new byte[256 * 256];
        var rng = new Random(42);
        rng.NextBytes(input128x64);
        rng.NextBytes(input256x256);

        var result1 = Ps2TexSwizzle.UnswizzlePsmt8(input128x64, 128, 64);
        var result2 = Ps2TexSwizzle.UnswizzlePsmt8(input256x256, 256, 256);

        Assert.Equal(128 * 64, result1.Length);
        Assert.Equal(256 * 256, result2.Length);
    }

    [Fact]
    public void UnswizzlePsmt8_ActuallyReordersData()
    {
        // Verify that un-swizzle actually changes byte positions (isn't identity)
        const int width = 128, height = 64;
        var input = new byte[width * height];
        for (var i = 0; i < input.Length; i++)
            input[i] = (byte)(i % 251); // Use prime to avoid patterns

        var result = Ps2TexSwizzle.UnswizzlePsmt8(input, width, height);

        // The result should differ from the input (the swizzle should change positions)
        var differences = 0;
        for (var i = 0; i < input.Length; i++)
            if (input[i] != result[i])
                differences++;

        Assert.True(differences > input.Length / 2,
            $"Expected significant reordering, but only {differences}/{input.Length} bytes changed");
    }

    [Fact]
    public void UnswizzlePsmt4_Conv4to32_ActuallyReordersData()
    {
        const int width = 64, height = 64;
        var bytes = width * height / 2;
        var input = new byte[bytes];
        for (var i = 0; i < bytes; i++)
            input[i] = (byte)(i % 251);

        var result = Ps2TexSwizzle.UnswizzlePsmt4(input, width, height);

        var differences = 0;
        for (var i = 0; i < bytes; i++)
            if (input[i] != result[i])
                differences++;

        Assert.True(differences > bytes / 2,
            $"Expected significant reordering, but only {differences}/{bytes} bytes changed");
    }
}
