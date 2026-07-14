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
        var off = 12;

        // Platform header (16 bytes): numBones, numQKeys@+4, numTKeys@+8,
        // numCustomAnimKeys@+12 — only numBones drives the parse; the key
        // totals are recomputed from the per-bone size tables below.
        var numBones = (int)BitConverter.ToUInt32(data[off..]);
        off += 16;

        // Alloc sizes: qAllocSize, tAllocSize@+4 — only qAllocSize is consumed.
        var qAllocSize = (int)BitConverter.ToUInt32(data[off..]);
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
