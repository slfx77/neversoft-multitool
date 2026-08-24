using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Walks a packed Nintendo GX display list: four command bytes to a word,
///     followed by every command's parameters in order.
/// </summary>
public static class NdsDisplayList
{
    /// <summary>
    ///     Called per command. <paramref name="parameterOffset" /> is the byte offset
    ///     of the command's first parameter, which is what the sub-object records
    ///     address when they name a TEXIMAGE_PARAM site to patch.
    /// </summary>
    public delegate void CommandHandler(byte opcode, ReadOnlySpan<uint> parameters, int parameterOffset);

    /// <summary>
    ///     Runs the list, calling <paramref name="handler" /> per command. Returns the
    ///     offset reached; equal to the section end only when every command's width
    ///     was right the whole way, which is the format's own consistency proof.
    /// </summary>
    public static int Walk(ReadOnlySpan<byte> data, int start, int end, CommandHandler? handler)
    {
        Span<uint> parameters = stackalloc uint[32];
        var at = start;
        while (at + 4 <= end)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            var need = 0;
            for (var i = 0; i < 4; i++)
            {
                var count = NdsGxCommand.ParameterCount((byte)(word >> (8 * i)));
                if (count < 0)
                    return at;
                need += count;
            }

            if (at + 4 + need * 4 > end)
                return at;

            var cursor = at + 4;
            for (var i = 0; i < 4; i++)
            {
                var opcode = (byte)(word >> (8 * i));
                var count = NdsGxCommand.ParameterCount(opcode);
                for (var p = 0; p < count; p++)
                    parameters[p] = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + p * 4)..]);
                handler?.Invoke(opcode, parameters[..count], cursor);
                cursor += count * 4;
            }

            at = cursor;
        }

        return at;
    }

    /// <summary>True when the file's declared display-list span parses exactly.</summary>
    public static bool Consumes(ReadOnlySpan<byte> data, NdsGeometryFile file)
    {
        return Walk(data, file.DisplayListStart, file.DisplayListEnd, null) == file.DisplayListEnd;
    }
}
