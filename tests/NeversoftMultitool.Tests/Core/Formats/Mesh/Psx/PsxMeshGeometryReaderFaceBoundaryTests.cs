using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxMeshGeometryReaderFaceBoundaryTests
{
    [Fact]
    public void ReadMesh_FaceDeclaredPastPhysicalEnd_ThrowsBeforePayloadRead()
    {
        using var stream = new MemoryStream(BuildV4Mesh(faceLength: 20, physicalLength: 76));
        using var reader = new BinaryReader(stream);

        var exception = Assert.Throws<EndOfStreamException>(() => ReadMesh(reader));

        Assert.Equal(
            "PSX face 0 at 0x3C declares 20 bytes, but only 16 physical bytes remain",
            exception.Message);
        Assert.Equal(64, stream.Position);
    }

    [Fact]
    public void ReadMesh_FaceEndingAtPhysicalEnd_IsAccepted()
    {
        using var stream = new MemoryStream(BuildV4Mesh(faceLength: 16, physicalLength: 76));
        using var reader = new BinaryReader(stream);

        var mesh = ReadMesh(reader);

        var face = Assert.Single(mesh.Faces);
        Assert.Equal((ushort)0x0510, face.CollisionFlags);
        var readInfo = Assert.Single(mesh.FaceReadInfos);
        Assert.Equal(16, readInfo.BytesConsumed);
        Assert.Equal(0, readInfo.UnderreadBytes);
        Assert.Equal(76, stream.Position);
    }

    [Fact]
    public void ReadMesh_InFileFacePadding_RemainsLegal()
    {
        using var stream = new MemoryStream(BuildV4Mesh(faceLength: 20, physicalLength: 80));
        using var reader = new BinaryReader(stream);

        var mesh = ReadMesh(reader);

        Assert.Single(mesh.Faces);
        var readInfo = Assert.Single(mesh.FaceReadInfos);
        Assert.Equal(16, readInfo.BytesConsumed);
        Assert.Equal(4, readInfo.UnderreadBytes);
        Assert.Equal(80, stream.Position);
    }

    [Fact]
    public void ReadMesh_PhysicalTrailerPastFace_RemainsUnconsumed()
    {
        using var stream = new MemoryStream(BuildV4Mesh(faceLength: 16, physicalLength: 80));
        using var reader = new BinaryReader(stream);

        var mesh = ReadMesh(reader);

        Assert.Single(mesh.Faces);
        Assert.Equal(76, stream.Position);
    }

    private static PsxMesh ReadMesh(BinaryReader reader)
    {
        return PsxMeshGeometryReader.ReadMesh(
            reader,
            version: 0x04,
            scaleDivisor: 4096f,
            textureHashes: [],
            attachmentVertices: null,
            hasMeshLodField: true);
    }

    private static byte[] BuildV4Mesh(ushort faceLength, int physicalLength)
    {
        var data = new byte[physicalLength];
        WriteUInt16(data, 2, 3); // vertices
        WriteUInt16(data, 4, 1); // normals
        WriteUInt16(data, 6, 1); // faces
        WriteUInt16(data, 24, 0x7FFF); // LOD depth
        WriteUInt16(data, 26, 0xFFFF); // no next LOD

        // Three 8-byte vertices occupy offsets 28..51. The zero-filled cells
        // are valid ordinary positions and vertex types.

        WriteUInt16(data, 56, 4096); // normal Z = 1.0

        WriteUInt16(data, 60, 0x0010); // untextured triangle
        WriteUInt16(data, 62, faceLength);
        data[64] = 0;
        data[65] = 1;
        data[66] = 2;
        data[67] = 0;
        data[68] = 0x80;
        data[69] = 0x80;
        data[70] = 0x80;
        data[71] = 0;
        WriteUInt16(data, 72, 0); // normal index
        WriteUInt16(data, 74, 0x0510); // collision/surface flags
        return data;
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    }
}
