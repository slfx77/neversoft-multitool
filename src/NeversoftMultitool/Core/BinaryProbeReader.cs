namespace NeversoftMultitool.Core;

internal static class BinaryProbeReader
{
    public static bool TryReadHeader(string filePath, int headerLength, out byte[] header, out int bytesRead)
    {
        if (headerLength < 0)
        {
            header = [];
            bytesRead = 0;
            return false;
        }

        header = new byte[headerLength];

        try
        {
            using var stream = File.OpenRead(filePath);
            return TryReadHeader(stream, header, out bytesRead);
        }
        catch
        {
            header = [];
            bytesRead = 0;
            return false;
        }
    }

    internal static bool TryReadHeader(Stream stream, int headerLength, out byte[] header, out int bytesRead)
    {
        if (headerLength < 0)
        {
            header = [];
            bytesRead = 0;
            return false;
        }

        header = new byte[headerLength];

        try
        {
            return TryReadHeader(stream, header, out bytesRead);
        }
        catch
        {
            header = [];
            bytesRead = 0;
            return false;
        }
    }

    private static bool TryReadHeader(Stream stream, byte[] header, out int bytesRead)
    {
        bytesRead = 0;
        while (bytesRead < header.Length)
        {
            var read = stream.Read(header, bytesRead, header.Length - bytesRead);
            if (read == 0)
                break;

            bytesRead += read;
        }

        return true;
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
