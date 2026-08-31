using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     The shared vocabulary of the later-revision next-gen scene format (Project 8
///     and Proving Ground): its record sizes, its big-endian readers, and the three
///     addressing decisions every other part of the reader has to agree on — where a
///     buffer descriptor is, what shape it has, and where a block pointer lands.
/// </summary>
internal static class NextGenSceneBinary
{
    internal const int SMeshRecordSize = 128;

    /// <summary>Stride of a position vertex on the descriptor path.</summary>
    internal const int VertexStride = 32;

    internal const int StripRestart = 0x7FFF;

    /// <summary>
    ///     48-byte buffer descriptor: the first batch's vertex count at +0x20, that
    ///     batch's bone palette at +0x24 (read but not consumed), two
    ///     unresolved-pointer filler slots, and the vertex data at +0x30. The
    ///     16-byte header introducing each later batch has the same shape.
    /// </summary>
    internal const int BufferDescriptorSize = 0x30;

    /// <summary>Proving Ground's PS3 descriptor; see <see cref="DescriptorShape" />.</summary>
    internal const int LongBufferDescriptorSize = 0x40;

    internal const int BatchHeaderSize = 16;

    /// <summary>Both the attribute block and the index block start their payload here.</summary>
    internal const int BlockHeaderSize = 0x20;

    /// <summary>
    ///     Attribute offsets are declared in a merged space whose first 32 bytes are
    ///     the position stream, so an offset of 32 means "the start of the attribute
    ///     entry".
    /// </summary>
    internal const int PositionStreamSize = 32;

    /// <summary>Poison written into slots the build tool left unresolved.</summary>
    internal const uint BadFood = 0xBAADF00D;

    private static ReadOnlySpan<byte> BufferMagic => [0xCA, 0xFE, 0xBA, 0xB4];

    /// <summary>
    ///     Where a descriptor states its first batch's count and where the vertex
    ///     data begins. Proving Ground's PlayStation 3 build uses a LONGER, 0x40-byte
    ///     descriptor — count at +0x30, data at +0x40 — and announces it by carrying
    ///     the <c>FACEF000 FACEF001</c> filler pair at +0x38/+0x3C. That marker is a
    ///     clean discriminator: 231 of 231 sampled PG-PS3 descriptors carry it and 0
    ///     of 708 across the other three builds do. The two readings are not
    ///     interchangeable — under the standard shape PG-PS3 reproduces 0 of 99
    ///     bounding spheres, and under the long shape it reproduces 99 of 99.
    /// </summary>
    internal static (int CountOffset, int DataOffset) DescriptorShape(byte[] data, int descriptor)
    {
        if (descriptor + LongBufferDescriptorSize <= data.Length &&
            ReadUInt32(data, descriptor + 0x38) == 0xFACEF000 &&
            ReadUInt32(data, descriptor + 0x3C) == 0xFACEF001)
        {
            return (0x30, LongBufferDescriptorSize);
        }

        return (0x20, BufferDescriptorSize);
    }

    /// <summary>
    ///     Where a block pointer actually lands.
    ///     <para>
    ///         Xbox 360 keeps its blocks in the scene file, scene-relative, with a
    ///         0x20-byte header before the payload. <b>PlayStation 3 moves the
    ///         attribute stream and the index buffer into a sibling VRAM companion,
    ///         and addresses it with the SAME pointers as RAW offsets from byte 0 —
    ///         no scene base and no header skip.</b> Measured with controls: indices
    ///         land exactly at the raw offset on 104/104 sampled Project 8 PS3 meshes,
    ///         while the same pointers read against a DIFFERENT file's companion score
    ///         0/103.
    ///     </para>
    /// </summary>
    internal static (byte[] Buffer, long Start) ResolveBlock(
        byte[] data, int scene, uint pointer, byte[]? vram)
    {
        return vram is null
            ? (data, scene + (long)pointer + BlockHeaderSize)
            : (vram, pointer);
    }

    /// <summary>
    ///     Every offset carrying the descriptor's own 16-byte sentinel — four
    ///     consecutive copies of <c>CAFEBAB4</c>.
    /// </summary>
    internal static HashSet<int> FindDescriptors(byte[] data)
    {
        var found = new HashSet<int>();
        var span = data.AsSpan();

        for (var o = 0; o + BufferDescriptorSize <= data.Length; o += 4)
        {
            if (!span.Slice(o, 4).SequenceEqual(BufferMagic))
                continue;

            if (o + 16 <= data.Length &&
                span.Slice(o + 4, 4).SequenceEqual(BufferMagic) &&
                span.Slice(o + 8, 4).SequenceEqual(BufferMagic) &&
                span.Slice(o + 12, 4).SequenceEqual(BufferMagic))
                found.Add(o);
        }

        return found;
    }

    /// <summary>
    ///     An 11/11/10 packed signed unit vector: x in bits 0-10 over 1023, y in bits
    ///     11-21 over 1023, z in bits 22-31 over 511.
    ///     <para>
    ///         The vertex carries three of these, at +0x10, +0x14 and +0x18, and all
    ///         three come out unit length on 100.000% of 97,296 sampled vertices
    ///         against a 6.3% control (the position word read the same way). <b>+0x10
    ///         is the normal</b>: its mean signed dot with the facet normal of the
    ///         triangles we emit is +0.909 in Project 8 and +0.923 in Proving Ground,
    ///         while the other two sit at ±0.007, i.e. orthogonal — consistent with a
    ///         tangent frame, though which of the two is the tangent is not something
    ///         this reader needs or claims. That the dot is POSITIVE is a second
    ///         result worth having: it says the strip triangulation's winding agrees
    ///         with the authored normals.
    ///     </para>
    /// </summary>
    internal static Vector3 UnpackUnitVector(uint packed)
    {
        return new Vector3(
            SignExtend(packed & 0x7FF, 11) / 1023f,
            SignExtend((packed >> 11) & 0x7FF, 11) / 1023f,
            SignExtend((packed >> 22) & 0x3FF, 10) / 511f);

        static int SignExtend(uint value, int bits)
        {
            var sign = 1u << (bits - 1);
            return (value & sign) != 0 ? (int)value - (1 << bits) : (int)value;
        }
    }

    internal static float HalfToSingle(ushort value)
    {
        return (float)BitConverter.UInt16BitsToHalf(value);
    }

    internal static uint ReadUInt32(byte[] d, int o)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    }

    internal static ushort ReadUInt16(byte[] d, int o)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
    }

    internal static float ReadSingle(byte[] d, int o)
    {
        return BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(o));
    }

    internal static Vector3 ReadVec3(byte[] d, int o)
    {
        return new Vector3(ReadSingle(d, o), ReadSingle(d, o + 4), ReadSingle(d, o + 8));
    }
}
