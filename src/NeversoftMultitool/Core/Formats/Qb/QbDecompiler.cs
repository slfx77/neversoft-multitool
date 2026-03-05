using System.Globalization;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Qb;

/// <summary>
///     Decompiles a parsed QB token stream back to readable script source text.
///     Reconstructs script/endscript blocks, if/else/endif, begin/repeat,
///     switch/case/endswitch, and data structures with proper indentation.
/// </summary>
public static class QbDecompiler
{
    /// <summary>
    ///     Decompile an entire QB file to source text.
    /// </summary>
    public static string Decompile(QbFile file)
    {
        var sb = new StringBuilder();
        var indent = 0;

        for (var i = 0; i < file.Tokens.Count; i++)
            i = EmitToken(file, file.Tokens, i, sb, ref indent);

        return sb.ToString();
    }

    /// <summary>
    ///     Decompile a single top-level item (script or global).
    /// </summary>
    public static string DecompileItem(QbFile file, QbItem item)
    {
        var sb = new StringBuilder();
        var indent = 0;

        for (var i = item.StartTokenIndex; i < item.EndTokenIndex && i < file.Tokens.Count; i++)
            i = EmitToken(file, file.Tokens, i, sb, ref indent);

        return sb.ToString();
    }

    /// <summary>
    ///     Returns true if the last character in the builder is a "word" character
    ///     that needs a space before the next word token.
    /// </summary>
    private static bool NeedsWordSeparator(StringBuilder sb)
    {
        if (sb.Length == 0) return false;
        var last = sb[^1];
        // No space needed after whitespace, opening brackets/parens, or operators that already include space
        return last != ' ' && last != '\t' && last != '\n' && last != '\r'
            && last != '(' && last != '<' && last != '[';
    }

    /// <summary>
    ///     Ensures a space separator before "word" tokens (names, values, keywords)
    ///     when they follow other word tokens without an intervening operator/separator.
    /// </summary>
    private static void WordSpace(StringBuilder sb)
    {
        if (NeedsWordSeparator(sb))
            sb.Append(' ');
    }

    private static int EmitToken(QbFile file, List<QbToken> tokens, int i, StringBuilder sb, ref int indent)
    {
        var t = tokens[i];

        switch (t.Type)
        {
            // Skip internal tokens
            case QbTokenType.EndOfFile:
            case QbTokenType.ChecksumName:
            case QbTokenType.Jump:
            case QbTokenType.DebugInfo:
                break;

            case QbTokenType.EndOfLine:
                sb.AppendLine();
                break;

            case QbTokenType.EndOfLineNumber:
                // Debug line number — skip (don't emit)
                break;

            // Keywords that decrease indent before emitting
            case QbTokenType.KeywordEndScript:
                indent = Math.Max(0, indent - 1);
                Indent(sb, indent);
                sb.Append("endscript");
                break;

            case QbTokenType.KeywordRepeat:
                indent = Math.Max(0, indent - 1);
                Indent(sb, indent);
                sb.Append("repeat");
                break;

            case QbTokenType.KeywordEndIf:
                indent = Math.Max(0, indent - 1);
                Indent(sb, indent);
                sb.Append("endif");
                break;

            case QbTokenType.KeywordElse:
                indent = Math.Max(0, indent - 1);
                Indent(sb, indent);
                sb.Append("else");
                indent++;
                break;

            case QbTokenType.KeywordElseIf:
                indent = Math.Max(0, indent - 1);
                Indent(sb, indent);
                sb.Append("elseif ");
                indent++;
                break;

            case QbTokenType.KeywordEndSwitch:
                indent = Math.Max(0, indent - 1);
                Indent(sb, indent);
                sb.Append("endswitch");
                break;

            // Keywords that emit then increase indent
            case QbTokenType.KeywordScript:
                Indent(sb, indent);
                sb.Append("script ");
                indent++;
                break;

            case QbTokenType.KeywordIf:
                Indent(sb, indent);
                sb.Append("if ");
                indent++;
                break;

            case QbTokenType.KeywordBegin:
                Indent(sb, indent);
                sb.Append("begin");
                indent++;
                break;

            case QbTokenType.KeywordSwitch:
                Indent(sb, indent);
                sb.Append("switch ");
                indent++;
                break;

            case QbTokenType.KeywordCase:
                indent = Math.Max(0, indent - 1);
                Indent(sb, indent);
                sb.Append("case ");
                indent++;
                break;

            case QbTokenType.KeywordDefault:
                indent = Math.Max(0, indent - 1);
                Indent(sb, indent);
                sb.Append("default");
                indent++;
                break;

            // Simple keywords
            case QbTokenType.KeywordReturn:
                WordSpace(sb);
                sb.Append("return ");
                break;

            case QbTokenType.KeywordBreak:
                Indent(sb, indent);
                sb.Append("break");
                break;

            case QbTokenType.KeywordAllArgs:
                WordSpace(sb);
                sb.Append("<...>");
                break;

            case QbTokenType.KeywordRandomRange:
            case QbTokenType.KeywordRandomRange2:
                WordSpace(sb);
                sb.Append("RandomRange ");
                break;

            // Data types — word tokens that need space separation
            case QbTokenType.Name:
            case QbTokenType.Enum:
                WordSpace(sb);
                sb.Append(file.ResolveName(t.NameChecksum));
                break;

            case QbTokenType.Arg:
                WordSpace(sb);
                sb.Append('<');
                break;

            case QbTokenType.Integer:
                WordSpace(sb);
                sb.Append(t.IntValue);
                break;

            case QbTokenType.HexInteger:
                WordSpace(sb);
                sb.Append($"0x{t.HexValue:X8}");
                break;

            case QbTokenType.Float:
                WordSpace(sb);
                sb.Append(t.FloatValue.ToString("G", CultureInfo.InvariantCulture));
                break;

            case QbTokenType.String:
                WordSpace(sb);
                sb.Append($"\"{EscapeString(t.StringValue ?? "")}\"");
                break;

            case QbTokenType.LocalString:
                WordSpace(sb);
                sb.Append($"'{EscapeString(t.StringValue ?? "")}'");
                break;

            case QbTokenType.Vector:
                WordSpace(sb);
                sb.Append($"({FormatFloat(t.FloatX)}, {FormatFloat(t.FloatY)}, {FormatFloat(t.FloatZ)})");
                break;

            case QbTokenType.Pair:
                WordSpace(sb);
                sb.Append($"({FormatFloat(t.FloatX)}, {FormatFloat(t.FloatY)})");
                break;

            // Structural
            case QbTokenType.StartStruct:
                WordSpace(sb);
                sb.Append("{ ");
                break;

            case QbTokenType.EndStruct:
                sb.Append("} ");
                break;

            case QbTokenType.StartArray:
                WordSpace(sb);
                sb.Append("[ ");
                break;

            case QbTokenType.EndArray:
                sb.Append("] ");
                break;

            // Operators
            case QbTokenType.Equals:
                sb.Append(" = ");
                break;

            case QbTokenType.SameAs:
                sb.Append(" == ");
                break;

            case QbTokenType.Dot:
                sb.Append('.');
                break;

            case QbTokenType.Comma:
                sb.Append(", ");
                break;

            case QbTokenType.Minus:
                sb.Append(" - ");
                break;

            case QbTokenType.Add:
                sb.Append(" + ");
                break;

            case QbTokenType.Divide:
                sb.Append(" / ");
                break;

            case QbTokenType.Multiply:
                sb.Append(" * ");
                break;

            case QbTokenType.OpenParenth:
                sb.Append('(');
                break;

            case QbTokenType.CloseParenth:
                sb.Append(')');
                break;

            case QbTokenType.LessThan:
                sb.Append(" < ");
                break;

            case QbTokenType.LessThanEqual:
                sb.Append(" <= ");
                break;

            case QbTokenType.GreaterThan:
                sb.Append(" > ");
                break;

            case QbTokenType.GreaterThanEqual:
                sb.Append(" >= ");
                break;

            case QbTokenType.Or:
                sb.Append(" | ");
                break;

            case QbTokenType.And:
                sb.Append(" & ");
                break;

            case QbTokenType.Xor:
                sb.Append(" ^ ");
                break;

            case QbTokenType.ShiftLeft:
                sb.Append(" << ");
                break;

            case QbTokenType.ShiftRight:
                sb.Append(" >> ");
                break;

            case QbTokenType.KeywordNot:
                WordSpace(sb);
                sb.Append("NOT ");
                break;

            case QbTokenType.KeywordAnd:
                sb.Append(" AND ");
                break;

            case QbTokenType.KeywordOr:
                sb.Append(" OR ");
                break;

            case QbTokenType.Colon:
                sb.Append(':');
                break;

            // Random blocks — emit header then let the body tokens follow naturally
            case QbTokenType.KeywordRandom:
            case QbTokenType.KeywordRandom2:
            case QbTokenType.KeywordRandomNoRepeat:
            case QbTokenType.KeywordRandomPermute:
                Indent(sb, indent);
                sb.Append(t.Type switch
                {
                    QbTokenType.KeywordRandom2 => "Random2",
                    QbTokenType.KeywordRandomNoRepeat => "RandomNoRepeat",
                    QbTokenType.KeywordRandomPermute => "RandomPermute",
                    _ => "Random"
                });
                if (t.RandomItemCount > 0)
                    sb.Append($"({t.RandomItemCount})");
                indent++;
                break;

            // Runtime functions (resolved at load time, shouldn't appear in .qb files)
            case QbTokenType.RuntimeCFunction:
            case QbTokenType.RuntimeMemberFunction:
                WordSpace(sb);
                sb.Append(file.ResolveName(t.NameChecksum));
                break;

            default:
                WordSpace(sb);
                sb.Append($"[{QbTokenInfo.GetTokenName(t.Type)}]");
                break;
        }

        return i;
    }

    private static void Indent(StringBuilder sb, int level)
    {
        // Only indent at start of line (after newline or at beginning)
        if (sb.Length == 0 || sb[^1] == '\n')
        {
            for (var i = 0; i < level; i++)
                sb.Append('\t');
        }
    }

    private static string FormatFloat(float f) =>
        f.ToString("G", CultureInfo.InvariantCulture);

    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
