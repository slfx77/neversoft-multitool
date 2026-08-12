using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     PLATFORM-flag SKA (cutscene object/camera masters): standard or
///     hi-res keys with optional OBJECTANIMDATA/PARTIALANIM preludes.
/// </summary>
internal static class SkaPlatformParser
{
    internal static SkaAnimation ParsePlatform(
        ReadOnlySpan<byte> data, uint version, uint flags, float duration)
    {
        const int platformHeaderOffset = 12;
        const int platformHeaderSize = 16;
        if (data.Length < platformHeaderOffset + platformHeaderSize)
            throw new InvalidDataException("SKA platform: header is truncated");

        var off = platformHeaderOffset;
        var isHiRes = (flags & SkaFile.FlagHiResFramePointers) != 0;

        // Platform header: numBones, numQKeys@+4, numTKeys@+8.
        var numBonesRaw = BitConverter.ToUInt32(data[off..]);
        var numQKeys = BitConverter.ToUInt32(data[(off + 4)..]);
        var numTKeys = BitConverter.ToUInt32(data[(off + 8)..]);
        if (numBonesRaw > int.MaxValue)
            throw new InvalidDataException($"SKA platform: bone count {numBonesRaw} is out of range");

        var numBones = (int)numBonesRaw;
        off += platformHeaderSize;

        // OBJECTANIMDATA (cutscene object anims): a numBones × u32 array of the QbKeys
        // of the objects each track drives, BEFORE the per-bone frame counts
        // (THUG BonedAnim.cpp plat_read_stream:1105-1111). Without skipping it the
        // per-bone counts read from the wrong offset and the whole file mis-parses.
        uint[]? boneNames = null;
        if ((flags & SkaFile.FlagObjectAnimData) != 0)
        {
            var boneNamesEnd = (long)off + 4L * numBones;
            if (boneNamesEnd > data.Length)
                throw new InvalidDataException("SKA platform: OBJECTANIMDATA bone-name table overruns file");

            boneNames = new uint[numBones];
            for (var i = 0; i < numBones; i++)
            {
                boneNames[i] = BitConverter.ToUInt32(data[off..]);
                off += 4;
            }
        }

        // PARTIALANIM: original bone count + a bit mask of which bones are present
        // (plat_read_stream:1117-1129), also before the per-bone frames.
        if ((flags & SkaFile.FlagPartialAnim) != 0)
        {
            if (off > data.Length - 4)
                throw new InvalidDataException("SKA platform: PARTIALANIM header overruns file");

            var originalBones = BitConverter.ToUInt32(data[off..]);
            if (originalBones == 0)
                throw new InvalidDataException("SKA platform: PARTIALANIM original bone count is zero");

            off += 4;
            var numMasks = ((long)originalBones + 31) / 32;
            var masksEnd = (long)off + numMasks * 4;
            if (masksEnd > data.Length)
                throw new InvalidDataException("SKA platform: PARTIALANIM mask overruns file");
            off = (int)masksEnd;
        }

        // Per-bone frame counts
        var countBytesPerBone = isHiRes ? 4 : 2;
        var countTableEnd = (long)off + (long)numBones * countBytesPerBone;
        if (countTableEnd > data.Length)
            throw new InvalidDataException("SKA platform: per-bone Q/T count table overruns file");

        var perBoneQCount = new int[numBones];
        var perBoneTCount = new int[numBones];
        long qCountTotal = 0;
        long tCountTotal = 0;
        if (isHiRes)
        {
            for (var i = 0; i < numBones; i++)
            {
                perBoneQCount[i] = BitConverter.ToInt16(data[off..]);
                perBoneTCount[i] = BitConverter.ToInt16(data[(off + 2)..]);
                if (perBoneQCount[i] < 0 || perBoneTCount[i] < 0)
                    throw new InvalidDataException($"SKA platform: bone {i} has a negative Q/T key count");
                qCountTotal += perBoneQCount[i];
                tCountTotal += perBoneTCount[i];
                off += 4;
            }
        }
        else
        {
            for (var i = 0; i < numBones; i++)
            {
                perBoneQCount[i] = data[off];
                perBoneTCount[i] = data[off + 1];
                qCountTotal += perBoneQCount[i];
                tCountTotal += perBoneTCount[i];
                off += 2;
            }
        }

        // 4-byte alignment
        if ((off & 3) != 0)
            off += 4 - (off & 3);

        var qKeySize = isHiRes ? 14 : 8; // CHiResAnimQKey or CStandardAnimQKey
        if (qCountTotal > numQKeys)
        {
            throw new InvalidDataException(
                $"SKA platform: per-bone Q key counts total {qCountTotal}, exceeding declared total {numQKeys}");
        }

        if (tCountTotal > numTKeys)
        {
            throw new InvalidDataException(
                $"SKA platform: per-bone T key counts total {tCountTotal}, exceeding declared total {numTKeys}");
        }

        var qBytes = (long)numQKeys * qKeySize;
        var tBytes = (long)numTKeys * qKeySize;
        var streamsEnd = (long)off + qBytes + tBytes;
        if (streamsEnd > data.Length)
        {
            throw new InvalidDataException(
                $"SKA platform: declared Q/T key blocks end at 0x{streamsEnd:X}, beyond file length 0x{data.Length:X}");
        }

        // Q keyframe data
        var qDataStart = off;
        var qDataEnd = (int)((long)off + qBytes);

        // T keyframe data begins immediately after the complete declared Q block.
        var tDataStart = qDataEnd;

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
                    rotKeys[k] = new SkaRotationKey(time, SkaFile.ReconstructQuat(qx, qy, qz, signBit));
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
                    rotKeys[k] = new SkaRotationKey(time, SkaFile.ReconstructQuat(qx, qy, qz, signBit));
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
                TranslationKeys = transKeys,
                BoneNameChecksum = boneNames?[bone]
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
