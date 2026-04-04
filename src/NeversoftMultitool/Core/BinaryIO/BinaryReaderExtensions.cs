using System.Text;

namespace NeversoftMultitool.Core.BinaryIO;

public static class BinaryReaderExtensions
{
    /// <summary>
    ///     Reads a null-terminated string from the stream.
    /// </summary>
    public static string ReadNullTerminatedString(this BinaryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }

        try
        {
            return Encoding.ASCII.GetString(bytes.ToArray());
        }
        catch
        {
            return Encoding.Latin1.GetString(bytes.ToArray());
        }
    }

    /// <summary>
    ///     Reads a fixed-length string, stopping at the first null byte.
    /// </summary>
    public static string ReadFixedString(this BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        var nullIndex = Array.IndexOf(bytes, (byte)0);
        var actualLength = nullIndex >= 0 ? nullIndex : length;

        try
        {
            return Encoding.ASCII.GetString(bytes, 0, actualLength);
        }
        catch
        {
            return Encoding.Latin1.GetString(bytes, 0, actualLength);
        }
    }

    /// <summary>
    ///     Aligns the stream position to the next multiple of n.
    /// </summary>
    public static void Align(this BinaryReader reader, int alignment)
    {
        var position = reader.BaseStream.Position;
        var remainder = position % alignment;
        if (remainder != 0)
        {
            reader.BaseStream.Position += alignment - remainder;
        }
    }

    /// <summary>
    ///     Custom Neversoft CRC32 (NOT standard zlib CRC32).
    /// </summary>
    public static uint Crc32Neversoft(byte[] data, uint start = 0xFFFFFFFF)
    {
        var result = start;
        foreach (var b in data)
        {
            var mask = result ^ b;
            for (var i = 0; i < 8; i++)
            {
                result = (result << 1) | (result >> 31);
                if ((mask & 1) != 0)
                {
                    result ^= 0xEDB88320;
                }

                mask >>= 1;
            }
        }

        return result;
    }
}
