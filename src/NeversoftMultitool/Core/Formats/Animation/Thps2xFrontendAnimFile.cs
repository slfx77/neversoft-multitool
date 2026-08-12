using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Bounded parser for the THPS2X frontend <c>.ANIM</c> format.
///
///     The 193-file shipped corpus establishes this balanced-forest grammar:
///     <code>
///     "Anm\0" u32(version=1) f32(duration) u32(rootCount)
///     Node[rootCount]
///
///     Node :=
///       u8(0xA1) u16(nameBytes) ascii[nameBytes]
///       f32 baseValues[12]
///       u32 keyCount u32 rawUnknown
///       Key[keyCount]
///       Node[children, while next byte is 0xA1]
///       u16(closingNameBytes) ascii[closingNameBytes]
///
///     Key := f32 values[9] u16 rawUnknown f32 trailingValue
///     </code>
///     The closing name terminates the current node, which makes nesting
///     deterministic without heuristic resynchronization.
/// </summary>
internal static class Thps2XFrontendAnimFile
{
    internal const uint SupportedVersion = 1;
    internal const byte NodeMarker = 0xA1;
    internal const int HeaderSize = 16;
    internal const int KeySize = 42;
    internal const int MaxDepth = 64;
    /// <summary>
    ///     Keeps a closing-string length byte disjoint from the 0xA1 child marker.
    ///     Shipped names reach 21 bytes and closing names reach 29 bytes.
    /// </summary>
    internal const int MaxStringBytes = 160;

    private const int FixedNodeBytesAfterName = 56;
    private const int MinimumNodeSize = 1 + 2 + FixedNodeBytesAfterName + 2;
    private static ReadOnlySpan<byte> Magic => "Anm\0"u8;

    internal static bool IsThps2XFrontendAnim(ReadOnlySpan<byte> data)
    {
        return data.Length >= HeaderSize
               && data[..4].SequenceEqual(Magic)
               && BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == SupportedVersion;
    }

    internal static Thps2XFrontendAnim Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Parse((ReadOnlySpan<byte>)data);
    }

    internal static Thps2XFrontendAnim Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: {data.Length}-byte file is shorter than the {HeaderSize}-byte header");
        if (!data[..4].SequenceEqual(Magic))
            throw new InvalidDataException("THPS2X frontend ANIM: missing Anm\\0 magic");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (version != SupportedVersion)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: unsupported version {version} (expected {SupportedVersion})");

        var duration = BinaryPrimitives.ReadSingleLittleEndian(data[8..]);
        if (!float.IsFinite(duration) || duration < 0f)
            throw new InvalidDataException($"THPS2X frontend ANIM: invalid duration {duration}");

        var rootCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var maximumNodes = (data.Length - HeaderSize) / MinimumNodeSize;
        if (rootCountRaw > maximumNodes)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: root count {rootCountRaw} cannot fit in {data.Length} bytes");

        var roots = new Thps2XFrontendAnimNode[checked((int)rootCountRaw)];
        var offset = HeaderSize;
        var nodeCount = 0;
        var keyCount = 0;
        for (var i = 0; i < roots.Length; i++)
            roots[i] = ParseNode(data, ref offset, 0, maximumNodes, ref nodeCount, ref keyCount);

        if (offset != data.Length)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: parsed roots end at 0x{offset:X}, " +
                $"but file length is 0x{data.Length:X}");

        return new Thps2XFrontendAnim
        {
            Version = version,
            Duration = duration,
            Roots = roots,
            SerializedSize = offset,
            NodeCount = nodeCount,
            KeyCount = keyCount
        };
    }

    private static Thps2XFrontendAnimNode ParseNode(
        ReadOnlySpan<byte> data,
        ref int offset,
        int depth,
        int maximumNodes,
        ref int nodeCount,
        ref int totalKeyCount)
    {
        if (depth >= MaxDepth)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: node nesting exceeds the {MaxDepth}-level safety limit");
        if (++nodeCount > maximumNodes)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: node count cannot fit in {data.Length} bytes");

        var start = offset;
        EnsureAvailable(data, offset, 1, "node marker");
        var marker = data[offset++];
        if (marker != NodeMarker)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: expected node marker 0x{NodeMarker:X2} " +
                $"at 0x{start:X}, found 0x{marker:X2}");

        var name = ReadString(data, ref offset, "node name");

        EnsureAvailable(data, offset, FixedNodeBytesAfterName, $"node '{name}' fixed fields");
        var baseValues = new float[12];
        for (var i = 0; i < baseValues.Length; i++)
        {
            baseValues[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + i * 4)..]);
            if (!float.IsFinite(baseValues[i]))
                throw new InvalidDataException(
                    $"THPS2X frontend ANIM: node '{name}' base value {i} is not finite");
        }

        var keyCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 48)..]);
        var rawUnknown32 = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 52)..]);
        offset += FixedNodeBytesAfterName;

        if (keyCountRaw > (uint)((data.Length - offset) / KeySize))
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: node '{name}' key count {keyCountRaw} overruns the file");

        var keys = new Thps2XFrontendAnimKey[checked((int)keyCountRaw)];
        for (var i = 0; i < keys.Length; i++)
        {
            var keyOffset = offset;
            var values = new float[9];
            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                values[valueIndex] = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + valueIndex * 4)..]);
                if (!float.IsFinite(values[valueIndex]))
                    throw new InvalidDataException(
                        $"THPS2X frontend ANIM: node '{name}' key {i} value {valueIndex} is not finite");
            }

            var rawUnknown16 = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 36)..]);
            var trailingValue = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 38)..]);
            if (!float.IsFinite(trailingValue))
                throw new InvalidDataException(
                    $"THPS2X frontend ANIM: node '{name}' key {i} trailing value is not finite");

            keys[i] = new Thps2XFrontendAnimKey
            {
                SerializedOffset = keyOffset,
                Values = values,
                RawUnknown16 = rawUnknown16,
                TrailingValue = trailingValue
            };
            offset += KeySize;
        }

        totalKeyCount = checked(totalKeyCount + keys.Length);

        var children = new List<Thps2XFrontendAnimNode>();
        while (offset < data.Length && data[offset] == NodeMarker)
            children.Add(ParseNode(data, ref offset, depth + 1, maximumNodes, ref nodeCount, ref totalKeyCount));

        var closingName = ReadString(data, ref offset, $"node '{name}' closing name");
        return new Thps2XFrontendAnimNode
        {
            SerializedOffset = start,
            SerializedSize = offset - start,
            Name = name,
            BaseValues = baseValues,
            RawUnknown32 = rawUnknown32,
            Keys = keys,
            Children = children.ToArray(),
            ClosingName = closingName
        };
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset, string context)
    {
        EnsureAvailable(data, offset, 2, $"{context} length");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        if (length > MaxStringBytes)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: {context} length {length} exceeds the " +
                $"{MaxStringBytes}-byte safety limit");

        EnsureAvailable(data, offset, length, context);
        var bytes = data.Slice(offset, length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is < 0x20 or > 0x7E)
                throw new InvalidDataException(
                    $"THPS2X frontend ANIM: {context} contains non-printable byte " +
                    $"0x{bytes[i]:X2} at 0x{offset + i:X}");
        }

        offset += length;
        return Encoding.ASCII.GetString(bytes);
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int size, string context)
    {
        if (offset < 0 || size < 0 || offset > data.Length - size)
            throw new InvalidDataException(
                $"THPS2X frontend ANIM: {context} overruns file at 0x{offset:X} " +
                $"(need {size} bytes, length 0x{data.Length:X})");
    }
}
