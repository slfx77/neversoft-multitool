using System.Buffers.Binary;

namespace NeversoftMultitool.Core.BinaryIO;

/// <summary>
///     Offset-addressed reader over a byte span, parameterized on byte order.
///     Lets one parse path serve both the little-endian (PS2/PC) and big-endian
///     (GameCube) variants of a format that is a field-for-field endian mirror
///     (THAW .ske/.ska and friends).
/// </summary>
internal readonly ref struct EndianSpanReader(ReadOnlySpan<byte> data, bool bigEndian)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public bool BigEndian { get; } = bigEndian;
    public int Length => _data.Length;

    public ushort U16(int pos) => BigEndian
        ? BinaryPrimitives.ReadUInt16BigEndian(_data[pos..])
        : BinaryPrimitives.ReadUInt16LittleEndian(_data[pos..]);

    public short S16(int pos) => BigEndian
        ? BinaryPrimitives.ReadInt16BigEndian(_data[pos..])
        : BinaryPrimitives.ReadInt16LittleEndian(_data[pos..]);

    public uint U32(int pos) => BigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(_data[pos..])
        : BinaryPrimitives.ReadUInt32LittleEndian(_data[pos..]);

    public int I32(int pos) => BigEndian
        ? BinaryPrimitives.ReadInt32BigEndian(_data[pos..])
        : BinaryPrimitives.ReadInt32LittleEndian(_data[pos..]);

    public float F32(int pos) => BigEndian
        ? BinaryPrimitives.ReadSingleBigEndian(_data[pos..])
        : BinaryPrimitives.ReadSingleLittleEndian(_data[pos..]);
}
