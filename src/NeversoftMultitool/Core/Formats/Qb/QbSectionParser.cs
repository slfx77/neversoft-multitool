using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Qb;

/// <summary>
///     Parses THAW-generation "sectioned" QB files (THAW PS2/PC/GC, same family as
///     Guitar Hero): 28-byte header (u32 0 + u32 fileSize + fixed 20-byte signature),
///     then a sequence of typed sections holding a value tree. Scripts are stored as
///     LZSS-compressed blobs of the classic THPS3-THUG2 token stream, extended with
///     token 0x4A (inline serialized struct with relative pointers).
///     Two "info" value encodings exist for the same layouts: the old set (THAW PS2,
///     THAW PC .wpc, THUG2 .xbx era) and the new set (THAW GC .ngc, GH Wii/PC/360).
///     GC files are big-endian throughout.
///     The parser synthesizes the equivalent raw token stream (globals become
///     NAME = value lines, scripts become script/endscript blocks) so the existing
///     <see cref="QbFile" /> indexing and <see cref="QbDecompiler" /> apply unchanged.
///     Reference: Nanook/Queen-Bee QueenBeeParser (PakFormat.cs type tables,
///     QbItemBase.cs section/item layouts, Lzss.cs), cloned at Sample/queen-bee.
///     Validated against THAW Rosetta files (bh_11_cam_pak_info.qb on PS2/PC/GC).
/// </summary>
public static class QbSectionParser
{
    private const uint OldSectionStringPointerOrStructItemQbKeyStringQs = 0x001A0400;

    private static readonly byte[] HeaderSignature =
    [
        0x1C, 0x08, 0x02, 0x04, 0x10, 0x04, 0x08, 0x0C, 0x0C, 0x08,
        0x02, 0x04, 0x14, 0x02, 0x04, 0x0C, 0x10, 0x10, 0x0C, 0x00
    ];

    /// <summary>(kind, new-encoding value, old-encoding value) — Queen-Bee PakFormat.cs.</summary>
    private static readonly (ItemKind Kind, uint NewValue, uint OldValue)[] TypeTable =
    [
        (ItemKind.SectionInteger, 0x00200100, 0x00010400),
        (ItemKind.SectionFloat, 0x00200200, 0x00020400),
        (ItemKind.SectionString, 0x00200300, 0x00030400),
        (ItemKind.SectionStringW, 0x00200400, 0x00040400),
        (ItemKind.SectionFloatsX2, 0x00200500, 0x00050400),
        (ItemKind.SectionFloatsX3, 0x00200600, 0x00060400),
        (ItemKind.SectionScript, 0x00200700, 0x00070400),
        (ItemKind.SectionStruct, 0x00200A00, 0x000A0400),
        (ItemKind.SectionArray, 0x00200C00, 0x000C0400),
        (ItemKind.SectionQbKey, 0x00200D00, 0x000D0400),
        (ItemKind.SectionQbKeyString, 0x00201A00, 0x00041A00),
        (ItemKind.SectionStringPointer, 0x00201B00, OldSectionStringPointerOrStructItemQbKeyStringQs),
        (ItemKind.SectionQbKeyStringQs, 0x00201C00, 0x001C0400),
        (ItemKind.ArrayInteger, 0x00010100, 0x00010100),
        (ItemKind.ArrayFloat, 0x00010200, 0x00020100),
        (ItemKind.ArrayString, 0x00010300, 0x00030100),
        (ItemKind.ArrayStringW, 0x00010400, 0x00040100),
        (ItemKind.ArrayFloatsX2, 0x00010500, 0x00050100),
        (ItemKind.ArrayFloatsX3, 0x00010600, 0x00060100),
        (ItemKind.ArrayStruct, 0x00010A00, 0x000A0100),
        (ItemKind.ArrayArray, 0x00010C00, 0x000C0100),
        (ItemKind.ArrayQbKey, 0x00010D00, 0x000D0100),
        (ItemKind.ArrayQbKeyString, 0x00011A00, 0x001A0100),
        (ItemKind.ArrayStringPointer, 0x00011B00, 0x001B0100),
        (ItemKind.ArrayQbKeyStringQs, 0x00011C00, 0x001C0100),
        (ItemKind.StructItemInteger, 0x00810000, 0x00000300),
        (ItemKind.StructItemFloat, 0x00820000, 0x00000500),
        (ItemKind.StructItemString, 0x00830000, 0x00000700),
        (ItemKind.StructItemStringW, 0x00840000, 0x00000900),
        (ItemKind.StructItemFloatsX2, 0x00850000, 0x00000B00),
        (ItemKind.StructItemFloatsX3, 0x00860000, 0x00000D00),
        (ItemKind.StructItemStruct, 0x008A0000, 0x00001500),
        (ItemKind.StructItemArray, 0x008C0000, 0x00001900),
        (ItemKind.StructItemQbKey, 0x008D0000, 0x00001B00),
        (ItemKind.StructItemQbKeyString, 0x009A0000, 0x00003500),
        (ItemKind.StructItemQbKeyStringQs, 0x009C0000, OldSectionStringPointerOrStructItemQbKeyStringQs),
        (ItemKind.Floats, 0x00010000, 0x00000100),
        (ItemKind.StructHeader, 0x00000100, 0x00010000)
    ];

    private static readonly Dictionary<uint, ItemKind> NewValues = BuildMap(true);
    private static readonly Dictionary<uint, ItemKind> OldValues = BuildMap(false);

    private static Dictionary<uint, ItemKind> BuildMap(bool newEncoding)
    {
        var map = new Dictionary<uint, ItemKind>();
        foreach (var (kind, newValue, oldValue) in TypeTable)
            map.TryAdd(newEncoding ? newValue : oldValue, kind);
        return map;
    }

    /// <summary>
    ///     True when the buffer carries the fixed 20-byte sectioned-QB header signature.
    /// </summary>
    public static bool IsSectionedQb(byte[] data)
    {
        return data.Length >= 28 && data.AsSpan(8, 20).SequenceEqual(HeaderSignature);
    }

    /// <summary>
    ///     Parses a sectioned QB file into the equivalent raw token stream.
    /// </summary>
    public static List<QbToken> ParseToTokens(byte[] data)
    {
        if (!IsSectionedQb(data))
            throw new InvalidDataException("Not a sectioned QB file (missing header signature)");

        // Endianness from the fileSize field (counts the whole file including this header).
        var sizeLe = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var sizeBe = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        bool bigEndian;
        uint fileSize;
        if (sizeLe >= 28 && sizeLe <= data.Length)
        {
            bigEndian = false;
            fileSize = sizeLe;
        }
        else if (sizeBe >= 28 && sizeBe <= data.Length)
        {
            bigEndian = true;
            fileSize = sizeBe;
        }
        else
        {
            throw new InvalidDataException(
                $"Implausible sectioned-QB fileSize (LE 0x{sizeLe:X}, BE 0x{sizeBe:X}, actual 0x{data.Length:X})");
        }

        var tokens = new List<QbToken>();
        if (fileSize <= 28)
        {
            tokens.Add(new QbToken { Type = QbTokenType.EndOfFile, Offset = 28 });
            return tokens;
        }

        // Info-value encoding from the first section value.
        var first = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(28))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
        bool newEncoding;
        if (NewValues.TryGetValue(first, out var kn) && IsSection(kn))
            newEncoding = true;
        else if (OldValues.TryGetValue(first, out var ko) && IsSection(ko))
            newEncoding = false;
        else
            throw new InvalidDataException($"Unknown first section value 0x{first:X8}");

        var ctx = new Context(data, bigEndian, newEncoding, 0);
        long pos = 28;
        while (pos + 20 <= fileSize)
            pos = EmitSection(ctx, pos, tokens);

        tokens.Add(new QbToken { Type = QbTokenType.EndOfFile, Offset = pos });
        return tokens;
    }

    private static bool IsSection(ItemKind kind)
    {
        return kind is >= ItemKind.SectionInteger and <= ItemKind.SectionQbKeyStringQs;
    }

    private static long EmitSection(Context ctx, long pos, List<QbToken> tokens)
    {
        var kind = ctx.KindAt(pos);
        if (kind is null || !IsSection(kind.Value))
            throw new InvalidDataException($"Unknown section value 0x{ctx.U32(pos):X8} at 0x{pos:X}");

        var key = ctx.U32(pos + 4);

        if (kind == ItemKind.SectionScript)
        {
            var ptr = ctx.Pointer(pos + 12);
            VerifyPointer(ptr, pos + 20, pos);
            tokens.Add(new QbToken { Type = QbTokenType.KeywordScript, Offset = pos });
            tokens.Add(new QbToken { Type = QbTokenType.Name, Offset = pos + 4, NameChecksum = key });
            return EmitScriptBody(ctx, pos + 20, tokens);
        }

        tokens.Add(new QbToken { Type = QbTokenType.Name, Offset = pos + 4, NameChecksum = key });
        tokens.Add(new QbToken { Type = QbTokenType.Equals, Offset = pos });

        long end;
        if (IsComplex(kind.Value))
        {
            var ptr = ctx.Pointer(pos + 12);
            VerifyPointer(ptr, pos + 20, pos);
            end = EmitComplexValue(ctx, kind.Value, pos + 20, tokens);
        }
        else
        {
            EmitSimpleValue(ctx, kind.Value, pos + 12, tokens);
            end = pos + 20;
        }

        tokens.Add(new QbToken { Type = QbTokenType.EndOfLine, Offset = end });
        return end;
    }

    private static bool IsComplex(ItemKind kind)
    {
        return kind is ItemKind.SectionString or ItemKind.SectionStringW or ItemKind.SectionArray
            or ItemKind.SectionStruct or ItemKind.SectionScript
            or ItemKind.SectionFloatsX2 or ItemKind.SectionFloatsX3
            or ItemKind.StructItemArray or ItemKind.StructItemFloatsX2 or ItemKind.StructItemFloatsX3
            or ItemKind.StructItemString or ItemKind.StructItemStringW or ItemKind.StructItemStruct
            or ItemKind.ArrayArray or ItemKind.ArrayString or ItemKind.ArrayStringW
            or ItemKind.ArrayStruct or ItemKind.ArrayFloatsX2 or ItemKind.ArrayFloatsX3;
    }

    /// <summary>Emits the value of a simple (inline u32) kind as one token.</summary>
    private static void EmitSimpleValue(Context ctx, ItemKind kind, long pos, List<QbToken> tokens)
    {
        var value = ctx.U32(pos);
        switch (kind)
        {
            case ItemKind.SectionInteger:
            case ItemKind.StructItemInteger:
            case ItemKind.ArrayInteger:
                tokens.Add(new QbToken { Type = QbTokenType.Integer, Offset = pos, IntValue = (int)value });
                break;

            case ItemKind.SectionFloat:
            case ItemKind.StructItemFloat:
            case ItemKind.ArrayFloat:
                tokens.Add(new QbToken
                {
                    Type = QbTokenType.Float, Offset = pos, FloatValue = BitConverter.Int32BitsToSingle((int)value)
                });
                break;

            case ItemKind.SectionQbKey:
            case ItemKind.SectionQbKeyString:
            case ItemKind.SectionQbKeyStringQs:
            case ItemKind.StructItemQbKey:
            case ItemKind.StructItemQbKeyString:
            case ItemKind.StructItemQbKeyStringQs:
            case ItemKind.ArrayQbKey:
            case ItemKind.ArrayQbKeyString:
            case ItemKind.ArrayQbKeyStringQs:
                tokens.Add(new QbToken { Type = QbTokenType.Name, Offset = pos, NameChecksum = value });
                break;

            case ItemKind.SectionStringPointer:
            case ItemKind.StructItemStringPointer:
            case ItemKind.ArrayStringPointer:
                tokens.Add(new QbToken { Type = QbTokenType.HexInteger, Offset = pos, HexValue = value });
                break;

            default:
                throw new InvalidDataException($"Kind {kind} is not a simple value kind");
        }
    }

    /// <summary>
    ///     Emits the data of a complex kind starting at <paramref name="pos" />; returns the end position.
    /// </summary>
    private static long EmitComplexValue(Context ctx, ItemKind kind, long pos, List<QbToken> tokens)
    {
        switch (kind)
        {
            case ItemKind.SectionStruct:
            case ItemKind.StructItemStruct:
                return EmitStruct(ctx, pos, true, tokens);

            case ItemKind.SectionArray:
            case ItemKind.StructItemArray:
            case ItemKind.ArrayArray:
                return EmitArray(ctx, pos, tokens);

            case ItemKind.SectionString:
            case ItemKind.SectionStringW:
            case ItemKind.StructItemString:
            case ItemKind.StructItemStringW:
                return EmitString(ctx, pos, tokens);

            case ItemKind.SectionFloatsX2:
            case ItemKind.StructItemFloatsX2:
                return EmitFloats(ctx, pos, 2, tokens);

            case ItemKind.SectionFloatsX3:
            case ItemKind.StructItemFloatsX3:
                return EmitFloats(ctx, pos, 3, tokens);

            default:
                throw new InvalidDataException($"Kind {kind} is not a complex value kind");
        }
    }

    /// <summary>Narrow, null-terminated string padded to 4-byte alignment.</summary>
    private static long EmitString(Context ctx, long pos, List<QbToken> tokens)
    {
        var nul = Array.IndexOf(ctx.Data, (byte)0, (int)pos);
        if (nul < 0)
            throw new InvalidDataException($"Unterminated string at 0x{pos:X}");

        tokens.Add(new QbToken
        {
            Type = QbTokenType.String,
            Offset = pos,
            StringValue = Encoding.Latin1.GetString(ctx.Data, (int)pos, nul - (int)pos)
        });
        return Align4(nul + 1);
    }

    /// <summary>[Floats marker][2-3 × f32] → Pair or Vector token.</summary>
    private static long EmitFloats(Context ctx, long pos, int count, List<QbToken> tokens)
    {
        if (ctx.KindAt(pos) != ItemKind.Floats)
            throw new InvalidDataException($"Expected Floats marker at 0x{pos:X}, got 0x{ctx.U32(pos):X8}");

        tokens.Add(new QbToken
        {
            Type = count == 3 ? QbTokenType.Vector : QbTokenType.Pair,
            Offset = pos,
            FloatX = ctx.F32(pos + 4),
            FloatY = ctx.F32(pos + 8),
            FloatZ = count == 3 ? ctx.F32(pos + 12) : 0f
        });
        return pos + 4 + 4L * count;
    }

    /// <summary>
    ///     Struct: optional StructHeader marker, first-item pointer, then a linked list of
    ///     items (each carries a next-item pointer; 0 terminates). Returns the end position
    ///     (after the last item, or after the header when empty).
    /// </summary>
    private static long EmitStruct(Context ctx, long pos, bool expectMarker, List<QbToken> tokens)
    {
        if (expectMarker)
        {
            if (ctx.KindAt(pos) != ItemKind.StructHeader)
                throw new InvalidDataException($"Expected StructHeader at 0x{pos:X}, got 0x{ctx.U32(pos):X8}");
            pos += 4;
        }

        var ptr = ctx.U32(pos) == 0 ? 0 : ctx.Pointer(pos);
        var end = pos + 4;
        if (ptr != 0)
            VerifyPointer(ptr, end, pos);

        tokens.Add(new QbToken { Type = QbTokenType.StartStruct, Offset = pos });

        while (ptr != 0)
        {
            // THAW-specific struct item: inline script. The info word packs the raw
            // (uncompressed) script byte length alongside the type marker; the data
            // is a bare little-endian token stream ending with an endscript token.
            if (TryGetInlineScriptSize(ctx.U32(ptr), ctx.NewEncoding, out var scriptSize))
            {
                var scriptKey = ctx.U32(ptr + 4);
                var scriptData = ctx.Pointer(ptr + 8);
                var scriptNext = ctx.U32(ptr + 12) == 0 ? 0 : ctx.Pointer(ptr + 12);
                VerifyPointer(scriptData, ptr + 16, ptr);
                if (scriptKey != 0)
                {
                    tokens.Add(new QbToken { Type = QbTokenType.Name, Offset = ptr + 4, NameChecksum = scriptKey });
                    tokens.Add(new QbToken { Type = QbTokenType.Equals, Offset = ptr + 4 });
                }

                tokens.Add(new QbToken { Type = QbTokenType.KeywordScript, Offset = scriptData });
                var body = ctx.Data.AsSpan((int)scriptData, scriptSize).ToArray();
                tokens.AddRange(QbFile.TokenizeScriptBody(body, ctx.BigEndian, ctx.NewEncoding));
                if (tokens[^1].Type != QbTokenType.KeywordEndScript)
                    tokens.Add(new QbToken { Type = QbTokenType.KeywordEndScript, Offset = scriptData + scriptSize });

                end = scriptData + scriptSize;
                ptr = scriptNext;
                continue;
            }

            var rawKind = ctx.U32(ptr);
            // The old PS2 encoding reuses 0x001A0400 for a top-level string
            // pointer and a QS key inside a struct. Resolve it from its grammar
            // context instead of the global type map's first matching entry.
            var kind = !ctx.NewEncoding && rawKind == OldSectionStringPointerOrStructItemQbKeyStringQs
                ? ItemKind.StructItemQbKeyStringQs
                : ctx.KindAt(ptr) ?? throw new InvalidDataException(
                    $"Unknown struct item value 0x{rawKind:X8} at 0x{ptr:X}");

            // Some files encode struct children with Array* values (Queen-Bee's
            // "array items" mode) — normalize to the equivalent struct item kind.
            kind = NormalizeStructItemKind(kind);

            var key = ctx.U32(ptr + 4);
            if (key != 0)
            {
                tokens.Add(new QbToken { Type = QbTokenType.Name, Offset = ptr + 4, NameChecksum = key });
                tokens.Add(new QbToken { Type = QbTokenType.Equals, Offset = ptr + 4 });
            }

            long next;
            if (IsComplex(kind))
            {
                var dataPtr = ctx.Pointer(ptr + 8);
                next = ctx.U32(ptr + 12) == 0 ? 0 : ctx.Pointer(ptr + 12);
                VerifyPointer(dataPtr, ptr + 16, ptr);
                end = EmitComplexValue(ctx, kind, ptr + 16, tokens);
            }
            else
            {
                EmitSimpleValue(ctx, kind, ptr + 8, tokens);
                next = ctx.U32(ptr + 12) == 0 ? 0 : ctx.Pointer(ptr + 12);
                end = ptr + 16;
            }

            ptr = next;
        }

        tokens.Add(new QbToken { Type = QbTokenType.EndStruct, Offset = end });
        return end;
    }

    /// <summary>
    ///     THAW inline-script struct item info words: new encoding 0x0087SSSS,
    ///     old encoding 0xSSSS0F00, where SSSS is the raw script byte length.
    /// </summary>
    private static bool TryGetInlineScriptSize(uint value, bool newEncoding, out int size)
    {
        if (newEncoding && value >> 16 == 0x0087)
        {
            size = (int)(value & 0xFFFF);
            return true;
        }

        if (!newEncoding && (value & 0xFFFF) == 0x0F00)
        {
            size = (int)(value >> 16);
            return true;
        }

        size = 0;
        return false;
    }

    private static ItemKind NormalizeStructItemKind(ItemKind kind)
    {
        return kind switch
        {
            ItemKind.ArrayInteger => ItemKind.StructItemInteger,
            ItemKind.ArrayFloat => ItemKind.StructItemFloat,
            ItemKind.ArrayString => ItemKind.StructItemString,
            ItemKind.ArrayStringW => ItemKind.StructItemStringW,
            ItemKind.ArrayFloatsX2 => ItemKind.StructItemFloatsX2,
            ItemKind.ArrayFloatsX3 => ItemKind.StructItemFloatsX3,
            ItemKind.ArrayStruct => ItemKind.StructItemStruct,
            ItemKind.ArrayArray => ItemKind.StructItemArray,
            ItemKind.ArrayQbKey => ItemKind.StructItemQbKey,
            ItemKind.ArrayQbKeyString => ItemKind.StructItemQbKeyString,
            ItemKind.ArrayStringPointer => ItemKind.StructItemStringPointer,
            ItemKind.ArrayQbKeyStringQs => ItemKind.StructItemQbKeyStringQs,
            _ => kind
        };
    }

    /// <summary>
    ///     Array: element-type marker, count, then (for count > 1) a pointer list, then
    ///     the elements. Simple element kinds store values inline. Returns the end position.
    /// </summary>
    private static long EmitArray(Context ctx, long pos, List<QbToken> tokens)
    {
        var elemKind = ctx.KindAt(pos) ?? throw new InvalidDataException(
            $"Unknown array element value 0x{ctx.U32(pos):X8} at 0x{pos:X}");
        var count = ctx.U32(pos + 4);

        tokens.Add(new QbToken { Type = QbTokenType.StartArray, Offset = pos });

        long end;
        if (elemKind == ItemKind.Floats)
        {
            // Bare floats pair — used by ArrayFloat elements in some GH-era files.
            end = EmitFloats(ctx, pos, 2, tokens);
        }
        else if (!IsComplex(elemKind) && elemKind != ItemKind.ArrayStruct && elemKind != ItemKind.StructHeader)
        {
            switch (count)
            {
                case 0:
                    end = pos + 8;
                    break;
                case 1:
                    EmitSimpleValue(ctx, elemKind, pos + 8, tokens);
                    end = pos + 12;
                    break;
                default:
                {
                    VerifyPointer(ctx.Pointer(pos + 8), pos + 12, pos);
                    for (long i = 0; i < count; i++)
                        EmitSimpleValue(ctx, elemKind, pos + 12 + 4 * i, tokens);
                    end = pos + 12 + 4L * count;
                    break;
                }
            }
        }
        else
        {
            end = pos + 12;
            if (count == 0)
            {
                // count + unused pointer slot
            }
            else
            {
                var elemPositions = new List<long>();
                if (count == 1)
                {
                    VerifyPointer(ctx.Pointer(pos + 8), pos + 12, pos);
                    elemPositions.Add(pos + 12);
                }
                else
                {
                    VerifyPointer(ctx.Pointer(pos + 8), pos + 12, pos);
                    for (long i = 0; i < count; i++)
                        elemPositions.Add(ctx.Pointer(pos + 12 + 4 * i));
                }

                foreach (var p in elemPositions)
                {
                    end = elemKind switch
                    {
                        ItemKind.ArrayStruct => EmitStruct(ctx, p, true, tokens),
                        ItemKind.ArrayArray => EmitArray(ctx, p, tokens),
                        ItemKind.ArrayString or ItemKind.ArrayStringW => EmitString(ctx, p, tokens),
                        ItemKind.ArrayFloatsX2 => EmitFloats(ctx, p, 2, tokens),
                        ItemKind.ArrayFloatsX3 => EmitFloats(ctx, p, 3, tokens),
                        _ => throw new InvalidDataException($"Unhandled array element kind {elemKind} at 0x{p:X}")
                    };
                }
            }
        }

        tokens.Add(new QbToken { Type = QbTokenType.EndArray, Offset = end });
        return end;
    }

    /// <summary>
    ///     Script section data: u32 unknown, u32 decompressed size, u32 compressed size,
    ///     then the (possibly LZSS-compressed) classic token stream, padded to 4 bytes.
    ///     The body already terminates with an endscript token.
    /// </summary>
    private static long EmitScriptBody(Context ctx, long pos, List<QbToken> tokens)
    {
        var decompressedSize = ctx.U32(pos + 4);
        var compressedSize = ctx.U32(pos + 8);
        if (pos + 12 + compressedSize > ctx.Data.Length)
            throw new InvalidDataException($"Script data at 0x{pos:X} overruns the file");

        var blob = ctx.Data.AsSpan((int)(pos + 12), (int)compressedSize);
        var body = compressedSize < decompressedSize
            ? LzssDecoder.Decode(blob, (int)decompressedSize)
            : blob.ToArray();

        var bodyTokens = QbFile.TokenizeScriptBody(body, ctx.BigEndian, ctx.NewEncoding);
        tokens.AddRange(bodyTokens);

        if (bodyTokens.Count == 0 || bodyTokens[^1].Type != QbTokenType.KeywordEndScript)
            tokens.Add(new QbToken { Type = QbTokenType.KeywordEndScript, Offset = pos });

        return Align4(pos + 12 + compressedSize);
    }

    /// <summary>
    ///     Token 0x4A payload inside THAW-generation script bodies: u16 byte length,
    ///     zero padding to the next 4-byte boundary, then a serialized struct whose
    ///     pointers are relative to the struct's own start. Emits the struct as
    ///     StartStruct/.../EndStruct tokens and returns the position after the struct.
    /// </summary>
    internal static int EmitInlineStruct(
        byte[] body, int pos, bool bigEndian, bool newEncoding, List<QbToken> tokens)
    {
        // pos points at the u16 length that follows the 0x4A token byte. The length
        // belongs to the (always little-endian) token stream; only the struct binary
        // itself follows the file's byte order.
        var length = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos));
        var structStart = (int)Align4(pos + 2);
        if (structStart >= body.Length)
            throw new InvalidDataException($"Inline struct at 0x{pos:X} overruns the script body");

        var ctx = new Context(body, bigEndian, newEncoding, structStart);
        var end = EmitStruct(ctx, structStart, true, tokens);

        // The stored length is the serialized struct size; trust the walked end but
        // never step backwards past the declared extent.
        var declaredEnd = structStart + length;
        return (int)Math.Max(end, Math.Min(declaredEnd, body.Length));
    }

    private static void VerifyPointer(long pointer, long expected, long at)
    {
        if (pointer != expected)
            throw new InvalidDataException(
                $"Pointer 0x{pointer:X} at 0x{at:X} does not match expected 0x{expected:X}");
    }

    private static long Align4(long value)
    {
        return (value + 3) & ~3L;
    }

    private enum ItemKind
    {
        SectionInteger,
        SectionFloat,
        SectionString,
        SectionStringW,
        SectionFloatsX2,
        SectionFloatsX3,
        SectionScript,
        SectionStruct,
        SectionArray,
        SectionQbKey,
        SectionQbKeyString,
        SectionStringPointer,
        SectionQbKeyStringQs,
        ArrayInteger,
        ArrayFloat,
        ArrayString,
        ArrayStringW,
        ArrayFloatsX2,
        ArrayFloatsX3,
        ArrayStruct,
        ArrayArray,
        ArrayQbKey,
        ArrayQbKeyString,
        ArrayStringPointer,
        ArrayQbKeyStringQs,
        StructItemInteger,
        StructItemFloat,
        StructItemString,
        StructItemStringW,
        StructItemFloatsX2,
        StructItemFloatsX3,
        StructItemStruct,
        StructItemArray,
        StructItemQbKey,
        StructItemQbKeyString,
        StructItemStringPointer,
        StructItemQbKeyStringQs,
        Floats,
        StructHeader
    }

    /// <summary>A section/item value tree reader bound to one buffer + encoding.</summary>
    private readonly struct Context(byte[] data, bool bigEndian, bool newEncoding, long pointerBase)
    {
        public byte[] Data { get; } = data;
        public bool BigEndian { get; } = bigEndian;
        public bool NewEncoding { get; } = newEncoding;

        /// <summary>Added to stored pointers (inline script structs use relative pointers).</summary>
        public long PointerBase { get; } = pointerBase;

        public uint U32(long pos)
        {
            return BigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)pos));
        }

        public float F32(long pos)
        {
            return BitConverter.Int32BitsToSingle((int)U32(pos));
        }

        public ItemKind? KindAt(long pos)
        {
            var map = NewEncoding ? NewValues : OldValues;
            return map.TryGetValue(U32(pos), out var kind) ? kind : null;
        }

        public long Pointer(long pos)
        {
            return PointerBase + U32(pos);
        }
    }
}
