using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.XbxScene;

public class XbxSkinVertexCodecValidationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(768)]
    public void ReadSkinningData_NoncanonicalBoneIndex_Throws(ushort storedIndex)
    {
        using var stream = BuildSkinningData(storedIndex);
        using var reader = new BinaryReader(stream);
        var vertex = new XbxVertex();

        var exception = Assert.Throws<InvalidDataException>(() =>
            XbxSkinVertexCodec.ReadSkinningData(reader, ref vertex));

        Assert.Contains(storedIndex.ToString(), exception.Message);
    }

    [Fact]
    public void ReadSkinningData_CanonicalBoneIndex_DecodesWithoutAliasing()
    {
        using var stream = BuildSkinningData(3);
        using var reader = new BinaryReader(stream);
        var vertex = new XbxVertex();

        XbxSkinVertexCodec.ReadSkinningData(reader, ref vertex);

        Assert.Equal(12, stream.Position);
        Assert.Equal(1, vertex.BoneIndex0);
        Assert.True(vertex.HasSkinData);
    }

    private static MemoryStream BuildSkinningData(ushort storedIndex)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u);
            writer.Write(storedIndex);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
        }

        stream.Position = 0;
        return stream;
    }
}
