using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.XbxScene;

public sealed class XbxSceneMaterialReaderTests
{
    [Fact]
    public void ReadMaterial_NegativeVcWibbleSequenceCount_IsRejected()
    {
        using var reader = CreateVcWibbleMaterialReader(sequenceCount: -1);

        var exception = Assert.Throws<InvalidDataException>(
            () => XbxSceneMaterialReader.ReadMaterial(reader));

        Assert.Equal(
            "Xbox material VC-wibble sequence count -1 is invalid",
            exception.Message);
    }

    [Fact]
    public void ReadMaterial_ZeroVcWibbleSequences_RemainsValid()
    {
        using var reader = CreateVcWibbleMaterialReader(sequenceCount: 0);

        var material = XbxSceneMaterialReader.ReadMaterial(reader);

        Assert.Single(material.Passes);
    }

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

    private static BinaryReader CreateVcWibbleMaterialReader(int sequenceCount)
    {
        var data = new byte[101];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 1); // pass count
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(36), XbxMaterialFlags.VcWibble); // first-pass flags
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(81), sequenceCount);
        return new BinaryReader(new MemoryStream(data, writable: false));
    }

    private static BinaryReader CreateMaterialReader(int passCount)
    {
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), passCount);
        return new BinaryReader(new MemoryStream(data, writable: false));
    }
}
