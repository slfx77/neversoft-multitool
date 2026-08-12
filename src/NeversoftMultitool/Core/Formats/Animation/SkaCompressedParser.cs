namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     THUG/THUG2 compressed SKA (USECOMPRESSTABLE): per-bone byte-sized
///     key blobs decoded via the shared compressed-key grammar.
/// </summary>
internal static class SkaCompressedParser
{
    internal static SkaAnimation ParseCompressed(
        ReadOnlySpan<byte> data, uint version, uint flags, float duration,
        SkaCompressTable? compressTable)
    {
        const int platformHeaderOffset = 12;
        const int allocationHeaderOffset = platformHeaderOffset + 16;
        const int sizeTablesOffset = allocationHeaderOffset + 8;

        if (data.Length < sizeTablesOffset)
            throw new InvalidDataException("SKA compressed: header is truncated");

        var off = 12;

        // Platform header (16 bytes): numBones, numQKeys@+4, numTKeys@+8,
        // numCustomAnimKeys@+12 — only numBones drives the parse; the key
        // totals are recomputed from the per-bone size tables below.
        var numBonesRaw = BitConverter.ToUInt32(data[off..]);
        var sizeTablesEnd = (long)sizeTablesOffset + 4L * numBonesRaw;
        if (numBonesRaw > int.MaxValue || sizeTablesEnd > data.Length)
        {
            throw new InvalidDataException(
                $"SKA compressed: {numBonesRaw}-bone Q/T size tables overrun file");
        }

        var numBones = (int)numBonesRaw;
        off += 16;

        // Alloc sizes bound the independent Q and T blobs. Per-bone tracks may
        // leave allocation slack, but they must never consume the other blob or
        // bytes following the declared animation data.
        var qAllocSize = BitConverter.ToUInt32(data[off..]);
        var tAllocSize = BitConverter.ToUInt32(data[(off + 4)..]);
        off += 8;

        // Per-bone frame byte sizes
        var perBoneQSize = new int[numBones];
        for (var i = 0; i < numBones; i++)
        {
            perBoneQSize[i] = BitConverter.ToUInt16(data[off..]);
            off += 2;
        }

        var perBoneTSize = new int[numBones];
        for (var i = 0; i < numBones; i++)
        {
            perBoneTSize[i] = BitConverter.ToUInt16(data[off..]);
            off += 2;
        }

        // 4-byte alignment
        if ((off & 3) != 0)
            off += 4 - (off & 3);

        var qSizeTotal = perBoneQSize.Sum(static size => (long)size);
        var tSizeTotal = perBoneTSize.Sum(static size => (long)size);
        if (qSizeTotal > qAllocSize)
        {
            throw new InvalidDataException(
                $"SKA compressed: Q size table totals {qSizeTotal} bytes, exceeding declared allocation {qAllocSize}");
        }

        if (tSizeTotal > tAllocSize)
        {
            throw new InvalidDataException(
                $"SKA compressed: T size table totals {tSizeTotal} bytes, exceeding declared allocation {tAllocSize}");
        }

        var streamsEnd = (long)off + qAllocSize + tAllocSize;
        if (streamsEnd > data.Length)
        {
            throw new InvalidDataException(
                $"SKA compressed: Q/T blobs end at 0x{streamsEnd:X}, beyond file length 0x{data.Length:X}");
        }

        // Q keyframe data blob
        var qDataStart = off;
        var qDataEnd = (int)((long)off + qAllocSize);

        // T keyframe data blob
        var tDataStart = qDataEnd;

        // Decode per-bone tracks
        var tracks = new SkaBoneTrack[numBones];
        var qOff = qDataStart;
        var tOff = tDataStart;

        for (var bone = 0; bone < numBones; bone++)
        {
            var qEnd = qOff + perBoneQSize[bone];
            var rotKeys = SkaCompressedKeyDecoders.DecodeCompressedQKeys(data, ref qOff, qEnd, compressTable);

            var tEnd = tOff + perBoneTSize[bone];
            var transKeys = SkaCompressedKeyDecoders.DecodeCompressedTKeys(data, ref tOff, tEnd, compressTable);

            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                RotationKeys = rotKeys,
                TranslationKeys = transKeys
            };
        }

        return new SkaAnimation
        {
            Version = version,
            Flags = flags,
            Duration = duration,
            BoneTracks = tracks
        };
    }
}
