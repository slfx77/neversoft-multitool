using System.Text;

namespace NeversoftMultitool.Core.Formats.Qb;

/// <summary>
///     Parsed representation of a QB file. Contains the raw token stream
///     plus pre-indexed top-level items (scripts and global definitions).
///     Format reference: THUG source Gel/Scripting/tokens.h, skiptoken.cpp, parse.cpp.
/// </summary>
public sealed class QbFile
{
    public string FileName { get; init; } = "";
    public List<QbToken> Tokens { get; init; } = [];

    /// <summary>
    ///     Checksum-to-name mappings defined within this file (from CHECKSUM_NAME tokens).
    /// </summary>
    public Dictionary<uint, string> LocalNames { get; init; } = new();

    /// <summary>
    ///     Pre-indexed top-level items: scripts and global definitions.
    /// </summary>
    public List<QbItem> Items { get; init; } = [];

    public int ScriptCount => Items.Count(i => i.Kind == QbItemKind.Script);
    public int GlobalCount => Items.Count(i => i.Kind == QbItemKind.Global);

    /// <summary>
    ///     Resolve a checksum to a name using local names first, then global QbKey dictionary.
    /// </summary>
    public string ResolveName(uint checksum)
    {
        if (LocalNames.TryGetValue(checksum, out var local))
            return local;
        var global = QbKey.TryResolve(checksum);
        if (global != null)
            return global;
        return $"#\"0x{checksum:X8}\"";
    }

    /// <summary>
    ///     Parse a QB file from disk.
    /// </summary>
    public static QbFile Parse(string path)
    {
        var data = File.ReadAllBytes(path);
        return Parse(data, Path.GetFileName(path));
    }

    /// <summary>
    ///     Parse a QB file from a byte array.
    /// </summary>
    public static QbFile Parse(byte[] data, string fileName = "")
    {
        var tokens = TokenizeAll(data);
        var localNames = CollectChecksumNames(tokens);
        var items = IndexTopLevelItems(tokens, localNames);

        return new QbFile
        {
            FileName = fileName,
            Tokens = tokens,
            LocalNames = localNames,
            Items = items
        };
    }

    /// <summary>
    ///     Pass 1: Walk the byte stream and produce a flat list of parsed tokens.
    ///     Token sizes from skiptoken.cpp.
    /// </summary>
    private static List<QbToken> TokenizeAll(byte[] data)
    {
        var tokens = new List<QbToken>();
        var pos = 0;
        var length = data.Length;

        while (pos < length)
        {
            var tokenByte = data[pos];
            if (tokenByte > 68)
            {
                // Unknown token — skip byte (resync strategy from Python parser)
                pos++;
                continue;
            }

            var type = (QbTokenType)tokenByte;
            var token = new QbToken { Type = type, Offset = pos };

            switch (type)
            {
                case QbTokenType.EndOfFile:
                    tokens.Add(token);
                    return tokens;

                // 1-byte tokens (no payload)
                case QbTokenType.EndOfLine:
                case QbTokenType.StartStruct:
                case QbTokenType.EndStruct:
                case QbTokenType.StartArray:
                case QbTokenType.EndArray:
                case QbTokenType.Equals:
                case QbTokenType.Dot:
                case QbTokenType.Comma:
                case QbTokenType.Minus:
                case QbTokenType.Add:
                case QbTokenType.Divide:
                case QbTokenType.Multiply:
                case QbTokenType.OpenParenth:
                case QbTokenType.CloseParenth:
                case QbTokenType.DebugInfo:
                case QbTokenType.SameAs:
                case QbTokenType.LessThan:
                case QbTokenType.LessThanEqual:
                case QbTokenType.GreaterThan:
                case QbTokenType.GreaterThanEqual:
                case QbTokenType.Array:
                case QbTokenType.KeywordBegin:
                case QbTokenType.KeywordRepeat:
                case QbTokenType.KeywordBreak:
                case QbTokenType.KeywordScript:
                case QbTokenType.KeywordEndScript:
                case QbTokenType.KeywordIf:
                case QbTokenType.KeywordElse:
                case QbTokenType.KeywordElseIf:
                case QbTokenType.KeywordEndIf:
                case QbTokenType.KeywordReturn:
                case QbTokenType.Undefined:
                case QbTokenType.KeywordAllArgs:
                case QbTokenType.Arg:
                case QbTokenType.Or:
                case QbTokenType.And:
                case QbTokenType.Xor:
                case QbTokenType.ShiftLeft:
                case QbTokenType.ShiftRight:
                case QbTokenType.KeywordRandomRange:
                case QbTokenType.KeywordRandomRange2:
                case QbTokenType.KeywordNot:
                case QbTokenType.KeywordAnd:
                case QbTokenType.KeywordOr:
                case QbTokenType.KeywordSwitch:
                case QbTokenType.KeywordEndSwitch:
                case QbTokenType.KeywordCase:
                case QbTokenType.KeywordDefault:
                case QbTokenType.Colon:
                case QbTokenType.At:
                    tokens.Add(token);
                    pos += 1;
                    break;

                // 5-byte tokens (1 + 4 bytes payload)
                case QbTokenType.Name:
                case QbTokenType.Enum:
                case QbTokenType.RuntimeCFunction:
                case QbTokenType.RuntimeMemberFunction:
                    if (pos + 5 > length) return tokens;
                    token.NameChecksum = BitConverter.ToUInt32(data, pos + 1);
                    tokens.Add(token);
                    pos += 5;
                    break;

                case QbTokenType.Integer:
                case QbTokenType.EndOfLineNumber:
                    if (pos + 5 > length) return tokens;
                    token.IntValue = BitConverter.ToInt32(data, pos + 1);
                    tokens.Add(token);
                    pos += 5;
                    break;

                case QbTokenType.HexInteger:
                    if (pos + 5 > length) return tokens;
                    token.HexValue = BitConverter.ToUInt32(data, pos + 1);
                    tokens.Add(token);
                    pos += 5;
                    break;

                case QbTokenType.Float:
                    if (pos + 5 > length) return tokens;
                    token.FloatValue = BitConverter.ToSingle(data, pos + 1);
                    tokens.Add(token);
                    pos += 5;
                    break;

                case QbTokenType.Jump:
                    if (pos + 5 > length) return tokens;
                    token.JumpOffset = BitConverter.ToInt32(data, pos + 1);
                    tokens.Add(token);
                    pos += 5;
                    break;

                // 13-byte: VECTOR (1 + 3×f32)
                case QbTokenType.Vector:
                    if (pos + 13 > length) return tokens;
                    token.FloatX = BitConverter.ToSingle(data, pos + 1);
                    token.FloatY = BitConverter.ToSingle(data, pos + 5);
                    token.FloatZ = BitConverter.ToSingle(data, pos + 9);
                    tokens.Add(token);
                    pos += 13;
                    break;

                // 9-byte: PAIR (1 + 2×f32)
                case QbTokenType.Pair:
                    if (pos + 9 > length) return tokens;
                    token.FloatX = BitConverter.ToSingle(data, pos + 1);
                    token.FloatY = BitConverter.ToSingle(data, pos + 5);
                    tokens.Add(token);
                    pos += 9;
                    break;

                // Variable: STRING/LOCALSTRING (1 + u32 length + data)
                case QbTokenType.String:
                case QbTokenType.LocalString:
                    if (pos + 5 > length) return tokens;
                    var strLen = (int)BitConverter.ToUInt32(data, pos + 1);
                    if (strLen <= 0 || strLen > 100000 || pos + 5 + strLen > length)
                    {
                        pos++;
                        continue; // Corrupted — resync
                    }

                    token.StringValue = Encoding.ASCII.GetString(
                        data, pos + 5, strLen).TrimEnd('\0');
                    tokens.Add(token);
                    pos += 5 + strLen;
                    break;

                // Variable: CHECKSUM_NAME (1 + u32 checksum + null-terminated string)
                case QbTokenType.ChecksumName:
                    if (pos + 5 > length) return tokens;
                    token.NameChecksum = BitConverter.ToUInt32(data, pos + 1);
                    var nullPos = Array.IndexOf(data, (byte)0, pos + 5);
                    if (nullPos == -1 || nullPos - (pos + 5) > 512)
                    {
                        pos++;
                        continue; // Corrupted — resync
                    }

                    token.StringValue = Encoding.ASCII.GetString(
                        data, pos + 5, nullPos - (pos + 5));
                    tokens.Add(token);
                    pos = nullPos + 1;
                    break;

                // Variable: RANDOM variants (1 + u32 count + 2*count weights + 4*count offsets)
                case QbTokenType.KeywordRandom:
                case QbTokenType.KeywordRandom2:
                case QbTokenType.KeywordRandomNoRepeat:
                case QbTokenType.KeywordRandomPermute:
                    if (pos + 5 > length) return tokens;
                    var numItems = (int)BitConverter.ToUInt32(data, pos + 1);
                    if (numItems <= 0 || numItems > 10000)
                    {
                        pos++;
                        continue; // Corrupted
                    }

                    var randomSize = 5 + 2 * numItems + 4 * numItems;
                    if (pos + randomSize > length) return tokens;

                    token.RandomItemCount = numItems;
                    var items = new (ushort Weight, int JumpOffset)[numItems];
                    var weightBase = pos + 5;
                    var offsetBase = weightBase + 2 * numItems;
                    for (var i = 0; i < numItems; i++)
                    {
                        items[i] = (
                            BitConverter.ToUInt16(data, weightBase + 2 * i),
                            BitConverter.ToInt32(data, offsetBase + 4 * i)
                        );
                    }

                    token.RandomItems = items;
                    tokens.Add(token);
                    pos += randomSize;
                    break;

                default:
                    // Unknown — skip byte
                    pos++;
                    break;
            }
        }

        return tokens;
    }

    /// <summary>
    ///     Pass 1b: Collect CHECKSUM_NAME pairs from the token list.
    /// </summary>
    private static Dictionary<uint, string> CollectChecksumNames(List<QbToken> tokens)
    {
        var names = new Dictionary<uint, string>();
        foreach (var t in tokens)
        {
            if (t.Type == QbTokenType.ChecksumName && t.StringValue != null)
                names.TryAdd(t.NameChecksum, t.StringValue);
        }

        return names;
    }

    /// <summary>
    ///     Pass 2: Index top-level items (scripts and global assignments).
    ///     Top-level structure from ParseQB in parse.cpp:
    ///     NAME EQUALS value... ENDOFLINE  → Global
    ///     KEYWORD_SCRIPT NAME body... KEYWORD_ENDSCRIPT → Script
    /// </summary>
    private static List<QbItem> IndexTopLevelItems(
        List<QbToken> tokens, Dictionary<uint, string> localNames)
    {
        var items = new List<QbItem>();
        var i = 0;

        while (i < tokens.Count)
        {
            var t = tokens[i];

            // Skip line separators, debug info, and checksum name registrations
            if (t.Type is QbTokenType.EndOfLine or QbTokenType.EndOfLineNumber
                or QbTokenType.ChecksumName or QbTokenType.DebugInfo)
            {
                i++;
                continue;
            }

            if (t.Type == QbTokenType.EndOfFile)
                break;

            // Global: NAME = value... (terminated by ENDOFLINE or next top-level token)
            if (t.Type == QbTokenType.Name)
            {
                var nameChecksum = t.NameChecksum;
                var startIdx = i;

                // Skip to end of this assignment (find ENDOFLINE at nesting depth 0)
                var depth = 0;
                i++;
                while (i < tokens.Count)
                {
                    var ct = tokens[i];
                    if (ct.Type is QbTokenType.StartStruct or QbTokenType.StartArray)
                        depth++;
                    else if (ct.Type is QbTokenType.EndStruct or QbTokenType.EndArray)
                        depth--;
                    else if (ct.Type == QbTokenType.EndOfLine && depth <= 0)
                    {
                        i++; // Include the ENDOFLINE
                        break;
                    }
                    else if (ct.Type == QbTokenType.EndOfFile)
                        break;

                    i++;
                }

                items.Add(new QbItem
                {
                    Kind = QbItemKind.Global,
                    NameChecksum = nameChecksum,
                    Name = ResolveName(nameChecksum, localNames),
                    StartTokenIndex = startIdx,
                    EndTokenIndex = i
                });
                continue;
            }

            // Script: KEYWORD_SCRIPT NAME body... KEYWORD_ENDSCRIPT
            if (t.Type == QbTokenType.KeywordScript)
            {
                var startIdx = i;
                i++; // Skip KEYWORD_SCRIPT

                uint nameChecksum = 0;
                if (i < tokens.Count && tokens[i].Type == QbTokenType.Name)
                {
                    nameChecksum = tokens[i].NameChecksum;
                    i++;
                }

                // Find matching KEYWORD_ENDSCRIPT
                while (i < tokens.Count && tokens[i].Type != QbTokenType.KeywordEndScript)
                    i++;
                if (i < tokens.Count)
                    i++; // Include KEYWORD_ENDSCRIPT

                items.Add(new QbItem
                {
                    Kind = QbItemKind.Script,
                    NameChecksum = nameChecksum,
                    Name = ResolveName(nameChecksum, localNames),
                    StartTokenIndex = startIdx,
                    EndTokenIndex = i
                });
                continue;
            }

            // Unknown top-level token — skip it
            i++;
        }

        return items;
    }

    private static string? ResolveName(uint checksum, Dictionary<uint, string> localNames)
    {
        if (checksum == 0) return null;
        if (localNames.TryGetValue(checksum, out var local))
            return local;
        return QbKey.TryResolve(checksum);
    }
}
