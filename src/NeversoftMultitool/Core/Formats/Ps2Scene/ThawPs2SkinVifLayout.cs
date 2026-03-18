using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawPs2SkinVifLayout
{
    internal static (int VifStart, List<int> SetupStarts) ResolveThawSetupBoundaries(
        byte[] data,
        int searchStart,
        int searchEnd)
    {
        var setupStarts = FindSetupBoundaries(data, searchStart, searchEnd);
        var rawFlushOffsets = FindRawSetupBoundaryFlushOffsets(data, searchStart, searchEnd);
        if (rawFlushOffsets.Count == 0)
            return (searchStart, setupStarts);

        var fallbackVifStart = rawFlushOffsets[0] + 4;
        var needsFallbackStart = setupStarts.Count == 0 || fallbackVifStart < setupStarts[0];
        if (!needsFallbackStart)
            return (searchStart, setupStarts);

        var rescannedSetupStarts = FindSetupBoundaries(data, fallbackVifStart, searchEnd);
        if (rescannedSetupStarts.Count > 0)
            return (fallbackVifStart, rescannedSetupStarts);

        var rawDirectOffsets = rawFlushOffsets
            .Select(flushOffset => flushOffset + 4)
            .ToList();
        return (fallbackVifStart, rawDirectOffsets);
    }

    internal static List<int> FindSetupBoundaries(byte[] data, int searchStart, int searchEnd)
    {
        var setupStarts = new List<int>();
        var position = searchStart;
        var previousWasFlush = false;

        while (position + 4 <= searchEnd && position + 4 <= data.Length)
        {
            var command = data[position + 3] & 0x7F;
            if (command is 0x10 or 0x11)
            {
                previousWasFlush = true;
            }
            else if (command is 0x50 or 0x51)
            {
                if (previousWasFlush)
                    setupStarts.Add(position);

                previousWasFlush = false;
            }
            else
            {
                previousWasFlush = false;
            }

            position = VifNextCode(data, position, searchEnd);
        }

        return setupStarts;
    }

    internal static List<int> FindRawDirectOffsets(byte[] data)
    {
        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);
        var entryTableEnd = (int)(32 + numObjects * 8 + totalMeshes2 * 64);
        var vifEnd = (int)Math.Min(dataSize + 16, data.Length);

        var directOffsets = new List<int>();
        for (var offset = entryTableEnd; offset + 8 <= vifEnd && offset + 8 <= data.Length; offset += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            if (word is not (0x10000000 or 0x11000000))
                continue;

            var nextCommand = data[offset + 7] & 0x7F;
            if (nextCommand is 0x50 or 0x51)
                directOffsets.Add(offset + 4);
        }

        return directOffsets;
    }

    internal static int VifNextCode(byte[] data, int offset, int end)
    {
        if (offset >= end || offset + 4 > data.Length)
            return end;

        var cmd = data[offset + 3];
        if ((cmd & 0x60) != 0x60)
        {
            return (cmd & 0x7F) switch
            {
                0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07
                    or 0x10 or 0x11 or 0x13 or 0x14 or 0x15 or 0x17 => offset + 4,
                0x20 => offset + 8,
                0x30 or 0x31 => offset + 20,
                0x4A => offset + (data[offset + 2] << 3) + 4,
                0x50 or 0x51 =>
                    offset + (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) << 4) + 4,
                _ => end
            };
        }

        var vn = (cmd >> 2) & 3;
        var vl = cmd & 3;
        var num = data[offset + 2];
        var dimension = vn + 1;
        var bitLength = 32 >> vl;
        var dataSize = ((bitLength * dimension * num + 31) >> 5) << 2;
        return offset + 4 + dataSize;
    }

    private static List<int> FindRawSetupBoundaryFlushOffsets(byte[] data, int searchStart, int searchEnd)
    {
        var flushOffsets = new List<int>();
        for (var offset = searchStart; offset + 8 <= searchEnd && offset + 8 <= data.Length; offset += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            if (word is not (0x10000000 or 0x11000000))
                continue;

            var nextCommand = data[offset + 7] & 0x7F;
            if (nextCommand is 0x50 or 0x51)
                flushOffsets.Add(offset);
        }

        return flushOffsets;
    }
}
