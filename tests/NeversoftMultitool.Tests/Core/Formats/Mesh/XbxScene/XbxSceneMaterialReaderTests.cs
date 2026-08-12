using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.XbxScene;

public sealed class XbxSceneMaterialReaderTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void ReadMaterial_InvalidPassCount_IsRejectedBeforeAllocation(int passCount)
    {
        using var reader = CreateMaterialReader(passCount);

        var exception = Assert.Throws<InvalidDataException>(
            () => XbxSceneMaterialReader.ReadMaterial(reader));

        Assert.Equal(
            $"Xbox material pass count {passCount} is outside 0..4",
            exception.Message);
    }

    [Fact]
    public void ReadMaterial_ZeroPasses_RemainsValid()
    {
        using var reader = CreateMaterialReader(0);

        var material = XbxSceneMaterialReader.ReadMaterial(reader);

        Assert.Equal(0, material.NumPasses);
        Assert.Empty(material.Passes);
    }

    private static BinaryReader CreateMaterialReader(int passCount)
    {
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), passCount);
        return new BinaryReader(new MemoryStream(data, writable: false));
    }
}
