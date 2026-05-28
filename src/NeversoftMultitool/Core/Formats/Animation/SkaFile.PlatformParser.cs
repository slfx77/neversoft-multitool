using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

internal static partial class SkaFile
{
    private static SkaAnimation ParsePlatform(
        ReadOnlySpan<byte> data, uint version, uint flags, float duration)
    {
        var off = 12;
        var isHiRes = (flags & FlagHiResFramePointers) != 0;
        var isCompressedTime = (flags & FlagCompressedTime) != 0;

        // Platform header
        var numBones = (int)BitConverter.ToUInt32(data[off..]);
        var numQKeys = (int)BitConverter.ToUInt32(data[(off + 4)..]);
        var numTKeys = (int)BitConverter.ToUInt32(data[(off + 8)..]);
        off += 16;

        // Per-bone frame counts
        var perBoneQCount = new int[numBones];
        var perBoneTCount = new int[numBones];
        if (isHiRes)
        {
            for (var i = 0; i < numBones; i++)
            {
                perBoneQCount[i] = BitConverter.ToInt16(data[off..]);
                perBoneTCount[i] = BitConverter.ToInt16(data[(off + 2)..]);
                off += 4;
            }
        }
        else
        {
            for (var i = 0; i < numBones; i++)
            {
                perBoneQCount[i] = data[off];
                perBoneTCount[i] = data[off + 1];
                off += 2;
            }
        }

        // 4-byte alignment
        if ((off & 3) != 0)
            off += 4 - (off & 3);

        // Q keyframe data
        var qKeySize = isHiRes ? 14 : 8; // CHiResAnimQKey or CStandardAnimQKey
        var qDataStart = off;
        off += numQKeys * qKeySize;

        // T keyframe data
        var tKeySize = isHiRes ? 14 : 8;
        var tDataStart = off;

        // Decode per-bone tracks
        var tracks = new SkaBoneTrack[numBones];
        var qOff = qDataStart;
        var tOff = tDataStart;

        for (var bone = 0; bone < numBones; bone++)
        {
            var rotKeys = new SkaRotationKey[perBoneQCount[bone]];
            for (var k = 0; k < perBoneQCount[bone]; k++)
            {
                if (isHiRes)
                {
                    var header = BitConverter.ToUInt16(data[qOff..]);
                    var timestamp = header & 0x3FFF;
                    var signBit = (header & 0x8000) != 0;
                    var qx = BitConverter.ToSingle(data[(qOff + 2)..]);
                    var qy = BitConverter.ToSingle(data[(qOff + 6)..]);
                    var qz = BitConverter.ToSingle(data[(qOff + 10)..]);
                    var time = timestamp / 60f;
                    rotKeys[k] = new SkaRotationKey(time, ReconstructQuat(qx, qy, qz, signBit));
                    qOff += 14;
                }
                else
                {
                    var header = BitConverter.ToUInt16(data[qOff..]);
                    var timestamp = header & 0x3FFF;
                    var signBit = (header & 0x8000) != 0;
                    var qx = BitConverter.ToInt16(data[(qOff + 2)..]) / 16384f;
                    var qy = BitConverter.ToInt16(data[(qOff + 4)..]) / 16384f;
                    var qz = BitConverter.ToInt16(data[(qOff + 6)..]) / 16384f;
                    var time = timestamp / 60f;
                    rotKeys[k] = new SkaRotationKey(time, ReconstructQuat(qx, qy, qz, signBit));
                    qOff += 8;
                }
            }

            var transKeys = new SkaTranslationKey[perBoneTCount[bone]];
            for (var k = 0; k < perBoneTCount[bone]; k++)
            {
                if (isHiRes)
                {
                    var timestamp = BitConverter.ToInt16(data[tOff..]);
                    var tx = BitConverter.ToSingle(data[(tOff + 2)..]);
                    var ty = BitConverter.ToSingle(data[(tOff + 6)..]);
                    var tz = BitConverter.ToSingle(data[(tOff + 10)..]);
                    var time = timestamp / 60f;
                    transKeys[k] = new SkaTranslationKey(time, new Vector3(tx, ty, tz));
                    tOff += 14;
                }
                else
                {
                    var timestamp = BitConverter.ToInt16(data[tOff..]);
                    var tx = BitConverter.ToInt16(data[(tOff + 2)..]) / 32f;
                    var ty = BitConverter.ToInt16(data[(tOff + 4)..]) / 32f;
                    var tz = BitConverter.ToInt16(data[(tOff + 6)..]) / 32f;
                    var time = timestamp / 60f;
                    transKeys[k] = new SkaTranslationKey(time, new Vector3(tx, ty, tz));
                    tOff += 8;
                }
            }

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
