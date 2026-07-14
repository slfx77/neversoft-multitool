using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     GameCube disc (GCM) filesystem: boot magic 0xC2339F3D at 0x1C, FST
///     offset/size at 0x424/0x428, 12-byte FST entries followed by the string
///     table. All fields big-endian; file offsets are raw byte positions
///     (GC discs fit in 32 bits), so this reads the image directly rather
///     than through a sector source.
/// </summary>
public static class GcmFileSystem
{
    private const uint Magic = 0xC2339F3D;

    public static bool IsGcm(Stream stream)
    {
        if (stream.Length < 0x440)
            return false;

        Span<byte> magic = stackalloc byte[4];
        stream.Position = 0x1C;
        stream.ReadExactly(magic);
        return BinaryPrimitives.ReadUInt32BigEndian(magic) == Magic;
    }

    public static List<DiscFileEntry> ReadFileList(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        stream.Position = 0x424;
        stream.ReadExactly(header);
        var fstOffset = BinaryPrimitives.ReadUInt32BigEndian(header);
        var fstSize = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);

        if (fstOffset == 0 || fstSize == 0 || fstOffset + fstSize > stream.Length)
            throw new InvalidDataException("GCM FST offset/size invalid.");

        var fst = new byte[fstSize];
        stream.Position = fstOffset;
        stream.ReadExactly(fst);

        var rootEntryCount = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8));
        var stringTableOffset = checked((int)(rootEntryCount * 12));
        if (stringTableOffset > fst.Length)
            throw new InvalidDataException("GCM FST is truncated.");

        var entries = new List<DiscFileEntry>();

        // Iterative walk: directory entries give the exclusive end index of
        // their children, so a path stack of (endIndex, path) tracks nesting.
        var pathStack = new Stack<(long EndIndex, string Path)>();
        pathStack.Push((rootEntryCount, ""));

        for (long i = 1; i < rootEntryCount; i++)
        {
            while (pathStack.Count > 1 && i >= pathStack.Peek().EndIndex)
                pathStack.Pop();

            var entry = fst.AsSpan(checked((int)(i * 12)), 12);
            var flagsAndName = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var isDirectory = (flagsAndName >> 24) != 0;
            var nameOffset = (int)(flagsAndName & 0xFFFFFF);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);

            var name = ReadName(fst, stringTableOffset + nameOffset);
            var directory = pathStack.Peek().Path;
            var fullPath = directory.Length == 0 ? name : $"{directory}/{name}";

            if (isDirectory)
            {
                pathStack.Push((length, fullPath));
            }
            else
            {
                entries.Add(new DiscFileEntry(directory, name, offset, length, false));
            }
        }

        return entries;
    }

    private static string ReadName(byte[] fst, int offset)
    {
        if (offset < 0 || offset >= fst.Length)
            return "";

        var end = offset;
        while (end < fst.Length && fst[end] != 0)
            end++;

        return Encoding.Latin1.GetString(fst.AsSpan(offset, end - offset));
    }
}
