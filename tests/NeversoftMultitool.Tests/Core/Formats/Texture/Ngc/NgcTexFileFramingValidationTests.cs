using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ngc;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

public sealed class NgcTexFileFramingValidationTests
{
    [Theory]
    [InlineData(16, uint.MaxValue, "NGC TEX entry 0 has invalid data size 4294967295.")]
    [InlineData(20, uint.MaxValue, "NGC TEX entry 0 has invalid data range (4294967295, 32).")]
    [InlineData(24, uint.MaxValue - 1, "NGC TEX entry 0 has invalid alpha offset 4294967294.")]
    public void Parse_OversizedUnsignedEntryField_ReturnsStableFailure(
        int entryFieldOffset,
        uint value,
        string expectedError)
    {
        var data = NgcTexTestBuilder.CreateDictionary();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8 + entryFieldOffset), value);

        var result = NgcTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Empty(result.Textures);
        Assert.Equal(expectedError, result.ErrorMessage);
    }

    [Fact]
    public void Parse_MissingAlphaSentinel_RemainsValid()
    {
        var data = NgcTexTestBuilder.CreateDictionary();

        var result = NgcTexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Textures);
    }
}
