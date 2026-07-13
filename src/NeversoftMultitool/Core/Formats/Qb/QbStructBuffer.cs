namespace NeversoftMultitool.Core.Formats.Qb;

/// <summary>
///     Reader for the CStruct/CArray binary stream produced by the engine's
///     <c>WriteToBuffer</c> serializers (THUG source <c>Gel/Scripting/utils.cpp</c>).
///     A struct is a sequence of components terminated by <c>ESYMBOLTYPE_NONE</c> (0);
///     each component is a type byte + u32 name checksum + type-dependent value.
///     Small integers/zero floats are width-compressed via dedicated symbol types.
///     Arrays are <c>elemType(u8) + count(u16)</c> + packed elements.
///     <para>
///     THUG2's cutscene "cifstruct" payloads (ext key 0x508AE2F2 = QbKey("cifstruct"))
///     ship in this format. The name-compression path (type-byte bits 7/6 index the
///     <c>WriteToBuffer_CompressionLookupTable_8/16</c> script arrays) is only taken by
///     in-game senders — the PC build tools write full names (<c>__PLAT_WN32__</c>
///     branch), and no corpus payload uses it — so it is rejected explicitly.
///     </para>
/// </summary>
public static class QbStructBuffer
{
    // ESymbolType (THUG Gel/Scripting/symboltype.h)
    private const byte TypeNone = 0;
    private const byte TypeInteger = 1;
    private const byte TypeFloat = 2;
    private const byte TypeString = 3;
    private const byte TypeLocalString = 4;
    private const byte TypePair = 5;
    private const byte TypeVector = 6;
    private const byte TypeStructure = 10;
    private const byte TypeArray = 12;
    private const byte TypeName = 13;
    private const byte TypeIntegerOneByte = 14;
    private const byte TypeIntegerTwoBytes = 15;
    private const byte TypeUnsignedIntegerOneByte = 16;
    private const byte TypeUnsignedIntegerTwoBytes = 17;
    private const byte TypeZeroInteger = 18;
    private const byte TypeZeroFloat = 19;
    private const byte MaskNameLookup = 0x80 | 0x40; // 8-/16-bit compression-table names

    /// <summary>One named component of a serialized struct.</summary>
    public sealed class Component
    {
        public uint NameChecksum { get; init; }

        /// <summary>
        ///     int / float / string / float[2] (pair) / float[3] (vector) /
        ///     uint (name checksum) / List&lt;Component&gt; (structure) / Array.
        /// </summary>
        public object? Value { get; init; }

        /// <summary>True when the value is a NAME (QbKey checksum) reference.</summary>
        public bool IsNameValue { get; init; }
    }

    /// <summary>A serialized CArray: homogeneous elements of one symbol type.</summary>
    public sealed class Array
    {
        public byte ElementType { get; init; }
        public List<object?> Elements { get; init; } = [];
    }

    /// <summary>
    ///     Parses a complete serialized struct; throws <see cref="InvalidDataException" />
    ///     if the stream is malformed or does not end exactly at the buffer end.
    /// </summary>
    public static List<Component> Parse(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var components = ParseStruct(data, ref pos);
        if (pos != data.Length)
            throw new InvalidDataException($"{data.Length - pos} trailing bytes after struct");
        return components;
    }

    private static List<Component> ParseStruct(ReadOnlySpan<byte> data, ref int pos)
    {
        var components = new List<Component>();
        while (true)
        {
            if (pos >= data.Length)
                throw new InvalidDataException("unterminated struct");
            var type = data[pos];
            if (type == TypeNone)
            {
                pos++;
                return components;
            }

            if ((type & MaskNameLookup) != 0)
                throw new InvalidDataException(
                    $"compression-table name at 0x{pos:X} (type byte 0x{type:X2}) — tables are game data");

            if (pos + 5 > data.Length)
                throw new InvalidDataException("truncated component header");
            var name = BitConverter.ToUInt32(data[(pos + 1)..]);
            pos += 5;

            object? value;
            var isName = false;
            switch (type)
            {
                case TypeInteger:
                    value = BitConverter.ToInt32(Slice(data, ref pos, 4));
                    break;
                case TypeFloat:
                    value = BitConverter.ToSingle(Slice(data, ref pos, 4));
                    break;
                case TypeString:
                case TypeLocalString:
                    value = ReadCString(data, ref pos);
                    break;
                case TypePair:
                {
                    var s = Slice(data, ref pos, 8);
                    value = new[] { BitConverter.ToSingle(s), BitConverter.ToSingle(s[4..]) };
                    break;
                }
                case TypeVector:
                {
                    var s = Slice(data, ref pos, 12);
                    value = new[]
                    {
                        BitConverter.ToSingle(s), BitConverter.ToSingle(s[4..]), BitConverter.ToSingle(s[8..])
                    };
                    break;
                }
                case TypeStructure:
                    value = ParseStruct(data, ref pos);
                    break;
                case TypeArray:
                    value = ParseArray(data, ref pos);
                    break;
                case TypeName:
                    value = BitConverter.ToUInt32(Slice(data, ref pos, 4));
                    isName = true;
                    break;
                case TypeIntegerOneByte:
                    value = (int)(sbyte)Slice(data, ref pos, 1)[0];
                    break;
                case TypeIntegerTwoBytes:
                    value = (int)BitConverter.ToInt16(Slice(data, ref pos, 2));
                    break;
                case TypeUnsignedIntegerOneByte:
                    value = (int)Slice(data, ref pos, 1)[0];
                    break;
                case TypeUnsignedIntegerTwoBytes:
                    value = (int)BitConverter.ToUInt16(Slice(data, ref pos, 2));
                    break;
                case TypeZeroInteger:
                    value = 0;
                    break;
                case TypeZeroFloat:
                    value = 0f;
                    break;
                default:
                    throw new InvalidDataException($"unknown component type {type} at 0x{pos - 5:X}");
            }

            components.Add(new Component { NameChecksum = name, Value = value, IsNameValue = isName });
        }
    }

    private static Array ParseArray(ReadOnlySpan<byte> data, ref int pos)
    {
        var header = Slice(data, ref pos, 3);
        var elemType = header[0];
        int count = BitConverter.ToUInt16(header[1..]);
        var array = new Array { ElementType = elemType };
        for (var i = 0; i < count; i++)
        {
            switch (elemType)
            {
                case TypeInteger:
                    array.Elements.Add(BitConverter.ToInt32(Slice(data, ref pos, 4)));
                    break;
                case TypeName:
                    array.Elements.Add(BitConverter.ToUInt32(Slice(data, ref pos, 4)));
                    break;
                case TypeFloat:
                    array.Elements.Add(BitConverter.ToSingle(Slice(data, ref pos, 4)));
                    break;
                case TypeString:
                case TypeLocalString:
                    array.Elements.Add(ReadCString(data, ref pos));
                    break;
                case TypePair:
                {
                    var s = Slice(data, ref pos, 8);
                    array.Elements.Add(new[] { BitConverter.ToSingle(s), BitConverter.ToSingle(s[4..]) });
                    break;
                }
                case TypeVector:
                {
                    var s = Slice(data, ref pos, 12);
                    array.Elements.Add(new[]
                    {
                        BitConverter.ToSingle(s), BitConverter.ToSingle(s[4..]), BitConverter.ToSingle(s[8..])
                    });
                    break;
                }
                case TypeStructure:
                    array.Elements.Add(ParseStruct(data, ref pos));
                    break;
                case TypeArray:
                    array.Elements.Add(ParseArray(data, ref pos));
                    break;
                case TypeNone:
                    break;
                default:
                    throw new InvalidDataException($"unknown array element type {elemType} at 0x{pos:X}");
            }
        }

        return array;
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, ref int pos, int length)
    {
        if (pos + length > data.Length)
            throw new InvalidDataException($"truncated value at 0x{pos:X}");
        var slice = data.Slice(pos, length);
        pos += length;
        return slice;
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int pos)
    {
        var end = data[pos..].IndexOf((byte)0);
        if (end < 0)
            throw new InvalidDataException($"unterminated string at 0x{pos:X}");
        var value = System.Text.Encoding.Latin1.GetString(data.Slice(pos, end));
        pos += end + 1;
        return value;
    }
}
