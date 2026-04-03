namespace NeversoftMultitool.Core;

internal static class BinaryProbeReader
{
    public static bool TryReadHeader(string filePath, int headerLength, out byte[] header, out int bytesRead)
    {
        header = new byte[headerLength];

        try
        {
            using var stream = File.OpenRead(filePath);
            bytesRead = stream.Read(header, 0, header.Length);
            return true;
        }
        catch
        {
            header = [];
            bytesRead = 0;
            return false;
        }
    }

    public static bool TryReadAllBytes(string filePath, out byte[] data)
    {
        try
        {
            data = File.ReadAllBytes(filePath);
            return true;
        }
        catch
        {
            data = [];
            return false;
        }
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BitConverter.ToUInt16(data[offset..]);
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BitConverter.ToUInt32(data[offset..]);
    }

    public static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BitConverter.ToUInt64(data[offset..]);
    }
}
