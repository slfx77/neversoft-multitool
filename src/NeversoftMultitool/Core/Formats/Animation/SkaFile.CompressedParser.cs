namespace NeversoftMultitool.Core.Formats.Animation;

internal static partial class SkaFile
{
    private static SkaAnimation ParseCompressed(
        ReadOnlySpan<byte> data, uint version, uint flags, float duration,
        SkaCompressTable? compressTable)
    {
        var off = 12;

        // Platform header (16 bytes)
        var numBones = (int)BitConverter.ToUInt32(data[off..]);
        var numQKeys = (int)BitConverter.ToUInt32(data[(off + 4)..]);
        var numTKeys = (int)BitConverter.ToUInt32(data[(off + 8)..]);
        // numCustomAnimKeys at off+12 (skip for now)
        off += 16;

        // Alloc sizes
        var qAllocSize = (int)BitConverter.ToUInt32(data[off..]);
        var tAllocSize = (int)BitConverter.ToUInt32(data[(off + 4)..]);
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

        // Q keyframe data blob
        var qDataStart = off;
        off += qAllocSize;

        // T keyframe data blob
        var tDataStart = off;

        // Decode per-bone tracks
        var tracks = new SkaBoneTrack[numBones];
        var qOff = qDataStart;
        var tOff = tDataStart;

        for (var bone = 0; bone < numBones; bone++)
        {
            var qEnd = qOff + perBoneQSize[bone];
            var rotKeys = DecodeCompressedQKeys(data, ref qOff, qEnd, duration,
                (flags & FlagCompressedTime) != 0, compressTable);

            var tEnd = tOff + perBoneTSize[bone];
            var transKeys = DecodeCompressedTKeys(data, ref tOff, tEnd, duration,
                (flags & FlagCompressedTime) != 0, compressTable);

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
