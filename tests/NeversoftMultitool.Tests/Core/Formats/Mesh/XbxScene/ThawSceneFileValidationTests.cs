using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.XbxScene;

public sealed class ThawSceneFileValidationTests
{
    [Fact]
    public void Parse_NegativeSectorCount_ThrowsInvalidData()
    {
        var data = BuildMinimalScene(sectorCount: -1);

        Assert.True(ThawSceneFile.IsThawScene(data));
        var error = Assert.Throws<InvalidDataException>(() => ThawSceneFile.Parse(data));

        Assert.Contains("sector count -1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ZeroSectorCount_ReturnsEmptyScene()
    {
        var scene = ThawSceneFile.Parse(BuildMinimalScene(sectorCount: 0));

        Assert.Single(scene.Materials);
        Assert.Empty(scene.Sectors);
        Assert.Empty(scene.Links);
    }

    private static byte[] BuildMinimalScene(int sectorCount)
    {
        // 32-byte file prefix + 16-byte material-list header + one 288-byte
        // zero-pass material, then BABEFACE and a 172-byte CScene header.
        var data = new byte[516];
        data[32] = 2;
        data[33] = 16;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(36, 4), 304);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(336, 4), 0xBABEFACE);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(344, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(346, 2), 172);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(472, 4), sectorCount);
        return data;
    }
}
