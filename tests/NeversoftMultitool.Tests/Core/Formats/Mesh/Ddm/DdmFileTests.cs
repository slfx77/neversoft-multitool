using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ddm;

public sealed class DdmFileTests
{
    private const int FileHeaderSize = 12;
    private const int ObjectTableSize = 8;
    private const int ObjectHeaderSize = 136;
    private const int ObjectOffset = FileHeaderSize + ObjectTableSize;

    [Fact]
    public void Parse_ObjectSpanSmallerThanFixedHeader_ThrowsInvalidDataException()
    {
        var data = CreateEmptyObjectDdm(ObjectHeaderSize - 1);

        Assert.Throws<InvalidDataException>(() => DdmFile.Parse(data));
    }

    [Fact]
    public void Parse_RecordPayloadPastDeclaredObjectSpan_ThrowsInvalidDataException()
    {
        var data = CreateEmptyObjectDdm(ObjectHeaderSize, ObjectHeaderSize + 152);
        WriteUInt32(data, ObjectOffset + 120, 1); // materialCount

        Assert.Throws<InvalidDataException>(() => DdmFile.Parse(data));
    }

    [Fact]
    public void Parse_ExactEmptyObjectSpan_ReturnsEmptyObject()
    {
        var file = DdmFile.Parse(CreateEmptyObjectDdm(ObjectHeaderSize));

        var obj = Assert.Single(file.Objects);
        Assert.Empty(obj.Materials);
        Assert.Empty(obj.Vertices);
        Assert.Empty(obj.Indices);
        Assert.Empty(obj.Splits);
    }

    [Fact]
    public void Parse_TruncatedObjectTable_ThrowsInvalidDataException()
    {
        var data = new byte[FileHeaderSize + ObjectTableSize - 1];
        WriteUInt32(data, 0, 1);
        WriteUInt32(data, 4, (uint)data.Length);
        WriteUInt32(data, 8, 1);

        Assert.Throws<InvalidDataException>(() => DdmFile.Parse(data));
    }

    [Fact]
    public void Parse_ObjectStartingInsideObjectTable_ThrowsInvalidDataException()
    {
        var data = CreateEmptyObjectDdm(ObjectHeaderSize);
        WriteUInt32(data, FileHeaderSize, FileHeaderSize);
        WriteUInt32(data, FileHeaderSize + 4, (uint)(data.Length - FileHeaderSize));

        Assert.Throws<InvalidDataException>(() => DdmFile.Parse(data));
    }

    [Fact]
    public void Parse_ObjectStartingInsideLaterObjectTableEntry_ThrowsInvalidDataException()
    {
        const int objectCount = 2;
        var objectDataStart = FileHeaderSize + objectCount * ObjectTableSize;
        var data = new byte[objectDataStart + ObjectHeaderSize];
        WriteUInt32(data, 0, 1);
        WriteUInt32(data, 4, (uint)data.Length);
        WriteUInt32(data, 8, objectCount);
        WriteUInt32(data, FileHeaderSize, FileHeaderSize + ObjectTableSize);
        WriteUInt32(data, FileHeaderSize + 4, (uint)(data.Length - FileHeaderSize - ObjectTableSize));
        WriteUInt32(data, FileHeaderSize + ObjectTableSize, (uint)objectDataStart);
        WriteUInt32(data, FileHeaderSize + ObjectTableSize + 4, ObjectHeaderSize);

        Assert.Throws<InvalidDataException>(() => DdmFile.Parse(data));
    }

    private static byte[] CreateEmptyObjectDdm(int declaredObjectSize, int backingObjectSize = ObjectHeaderSize)
    {
        var data = new byte[ObjectOffset + backingObjectSize];
        WriteUInt32(data, 0, 1);
        WriteUInt32(data, 4, (uint)data.Length);
        WriteUInt32(data, 8, 1);
        WriteUInt32(data, FileHeaderSize, (uint)ObjectOffset);
        WriteUInt32(data, FileHeaderSize + 4, (uint)declaredObjectSize);
        return data;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }
}
