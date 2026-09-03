using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     PLATFORM-flag SKA (cutscene object/camera masters): standard or
///     hi-res keys with optional OBJECTANIMDATA/PARTIALANIM preludes.
/// </summary>
internal static class SkaPlatformParser
{
    internal static SkaAnimation ParsePlatform(
        ReadOnlySpan<byte> data, uint version, uint flags, float duration,
        bool requireExactContainer = false)
    {
        const int platformHeaderOffset = 12;
        const int platformHeaderSize = 16;
        if (data.Length < platformHeaderOffset + platformHeaderSize)
            throw new InvalidDataException("SKA platform: header is truncated");

        var off = platformHeaderOffset;
        // BonedAnim.cpp's is_hires() is driven by CAMERA/OBJECT data. Bit 22
        // independently selects 16-bit rather than 8-bit per-bone key counts.
        var hasHiResKeys = (flags & (SkaFile.FlagCameraData | SkaFile.FlagObjectAnimData)) != 0;
        var hasWideFramePointers = (flags & SkaFile.FlagHiResFramePointers) != 0;

        var numBonesRaw = BitConverter.ToUInt32(data[off..]);
        var numQKeys = BitConverter.ToUInt32(data[(off + 4)..]);
        var numTKeys = BitConverter.ToUInt32(data[(off + 8)..]);
        var numCustomKeys = BitConverter.ToUInt32(data[(off + 12)..]);
        if (numBonesRaw > int.MaxValue || numQKeys > int.MaxValue || numTKeys > int.MaxValue)
            throw new InvalidDataException("SKA platform: a header count is out of range");

        var numBones = (int)numBonesRaw;
        off += platformHeaderSize;

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

        var countBytesPerBone = hasWideFramePointers ? 4 : 2;
        var countTableEnd = (long)off + (long)numBones * countBytesPerBone;
        if (countTableEnd > data.Length)
            throw new InvalidDataException("SKA platform: per-bone Q/T count table overruns file");

        var perBoneQCount = new int[numBones];
        var perBoneTCount = new int[numBones];
        long qCountTotal = 0;
        long tCountTotal = 0;
        if (hasWideFramePointers)
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

        if (qCountTotal > numQKeys || requireExactContainer && qCountTotal != numQKeys)
        {
            throw new InvalidDataException(
                $"SKA platform: per-bone Q key counts total {qCountTotal}, " +
                $"not compatible with declared total {numQKeys}");
        }

        if (tCountTotal > numTKeys || requireExactContainer && tCountTotal != numTKeys)
        {
            throw new InvalidDataException(
                $"SKA platform: per-bone T key counts total {tCountTotal}, " +
                $"not compatible with declared total {numTKeys}");
        }

        // THPS4 PC/PS2 platform files retain the loader buffer's two-byte base
        // bias in the serialized offsets: a one-bone byte-count table starts its
        // 16-byte keys at file offset 30, not 32. Older fixtures/files can use
        // conventional file-relative alignment, so try both and accept only a
        // layout whose complete key/custom stream consumes EOF exactly.
        var alignedOff = Align4(off);
        Span<int> candidates = stackalloc int[2];
        if (hasHiResKeys && !hasWideFramePointers)
        {
            candidates[0] = off;
            candidates[1] = alignedOff;
        }
        else
        {
            candidates[0] = alignedOff;
            candidates[1] = off;
        }

        InvalidDataException? firstError = null;
        for (var i = 0; i < candidates.Length; i++)
        {
            var keyDataStart = candidates[i];
            if (i > 0 && keyDataStart == candidates[0])
                continue;

            try
            {
                return ParseStreams(
                    data, version, flags, duration, hasHiResKeys,
                    numBones, numQKeys, numTKeys, numCustomKeys,
                    perBoneQCount, perBoneTCount, boneNames, keyDataStart,
                    requireExactContainer);
            }
            catch (InvalidDataException ex)
            {
                firstError ??= ex;
            }
        }

        if (alignedOff == off && firstError != null)
            throw firstError;

        throw new InvalidDataException(
            $"SKA platform: no exact key-stream layout was valid ({firstError?.Message ?? "unknown error"})",
            firstError);
    }

    private static SkaAnimation ParseStreams(
        ReadOnlySpan<byte> data,
        uint version,
        uint flags,
        float duration,
        bool hasHiResKeys,
        int numBones,
        uint numQKeys,
        uint numTKeys,
        uint numCustomKeys,
        int[] perBoneQCount,
        int[] perBoneTCount,
        uint[]? boneNames,
        int keyDataStart,
        bool requireExactContainer)
    {
        var keySize = hasHiResKeys ? 16 : 8;
        var qBytes = (long)numQKeys * keySize;
        var tBytes = (long)numTKeys * keySize;
        var streamsEnd = (long)keyDataStart + qBytes + tBytes;
        if (keyDataStart < 0 || streamsEnd > data.Length)
        {
            throw new InvalidDataException(
                $"SKA platform: declared Q/T key blocks end at 0x{streamsEnd:X}, " +
                $"beyond file length 0x{data.Length:X}");
        }

        var qDataEnd = checked(keyDataStart + (int)qBytes);
        var qOff = keyDataStart;
        var tOff = qDataEnd;
        var tracks = new SkaBoneTrack[numBones];

        for (var bone = 0; bone < numBones; bone++)
        {
            var rotKeys = new SkaRotationKey[perBoneQCount[bone]];
            for (var k = 0; k < rotKeys.Length; k++)
            {
                var header = BitConverter.ToUInt16(data[qOff..]);
                var timestamp = header & 0x7FFF;
                var signBit = (header & 0x8000) != 0;
                float qx;
                float qy;
                float qz;
                if (hasHiResKeys)
                {
                    ValidateHiResPadding(data, qOff, "Q", bone, k);
                    qx = BitConverter.ToSingle(data[(qOff + 4)..]);
                    qy = BitConverter.ToSingle(data[(qOff + 8)..]);
                    qz = BitConverter.ToSingle(data[(qOff + 12)..]);
                    if (!float.IsFinite(qx) || !float.IsFinite(qy) || !float.IsFinite(qz))
                    {
                        throw new InvalidDataException(
                            $"SKA platform: bone {bone} high-resolution Q key {k} contains a non-finite component");
                    }
                }
                else
                {
                    qx = BitConverter.ToInt16(data[(qOff + 2)..]) / 16384f;
                    qy = BitConverter.ToInt16(data[(qOff + 4)..]) / 16384f;
                    qz = BitConverter.ToInt16(data[(qOff + 6)..]) / 16384f;
                }

                rotKeys[k] = new SkaRotationKey(
                    timestamp / 60f,
                    SkaFile.ReconstructQuat(qx, qy, qz, signBit));
                qOff += keySize;
            }

            var transKeys = new SkaTranslationKey[perBoneTCount[bone]];
            for (var k = 0; k < transKeys.Length; k++)
            {
                var timestamp = BitConverter.ToInt16(data[tOff..]);
                if (timestamp < 0)
                    throw new InvalidDataException($"SKA platform: bone {bone} T key {k} has a negative timestamp");

                float tx;
                float ty;
                float tz;
                if (hasHiResKeys)
                {
                    ValidateHiResPadding(data, tOff, "T", bone, k);
                    tx = BitConverter.ToSingle(data[(tOff + 4)..]);
                    ty = BitConverter.ToSingle(data[(tOff + 8)..]);
                    tz = BitConverter.ToSingle(data[(tOff + 12)..]);
                    if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
                    {
                        throw new InvalidDataException(
                            $"SKA platform: bone {bone} high-resolution T key {k} contains a non-finite component");
                    }
                }
                else
                {
                    tx = BitConverter.ToInt16(data[(tOff + 2)..]) / 32f;
                    ty = BitConverter.ToInt16(data[(tOff + 4)..]) / 32f;
                    tz = BitConverter.ToInt16(data[(tOff + 6)..]) / 32f;
                }

                transKeys[k] = new SkaTranslationKey(timestamp / 60f, new Vector3(tx, ty, tz));
                tOff += keySize;
            }

            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                RotationKeys = rotKeys,
                TranslationKeys = transKeys,
                BoneNameChecksum = boneNames?[bone]
            };
        }

        if (requireExactContainer && (qOff != qDataEnd || tOff != streamsEnd))
            throw new InvalidDataException("SKA platform: per-bone counts did not consume the declared Q/T streams");

        var customKeys = ParseCustomTail(
            data, checked((int)streamsEnd), numCustomKeys, requireExactContainer);
        return new SkaAnimation
        {
            Version = version,
            Flags = flags,
            Duration = duration,
            BoneTracks = tracks,
            CustomKeys = customKeys
        };
    }

    private static SkaCustomKey[] ParseCustomTail(
        ReadOnlySpan<byte> data, int streamEnd, uint numCustomKeys,
        bool requireExactContainer)
    {
        try
        {
            return SkaCustomKeyParser.ParseLittleEndianExact(
                data, streamEnd, numCustomKeys, "SKA platform",
                allowTerminalAlignmentPadding: !requireExactContainer);
        }
        catch (InvalidDataException rawError)
        {
            var aligned = Align4(streamEnd);
            if (aligned == streamEnd || aligned > data.Length)
                throw;
            for (var i = streamEnd; i < aligned; i++)
            {
                if (data[i] != 0)
                    throw;
            }

            try
            {
                return SkaCustomKeyParser.ParseLittleEndianExact(
                    data, aligned, numCustomKeys, "SKA platform",
                    allowTerminalAlignmentPadding: !requireExactContainer);
            }
            catch (InvalidDataException)
            {
                throw rawError;
            }
        }
    }

    private static void ValidateHiResPadding(
        ReadOnlySpan<byte> data, int offset, string kind, int bone, int key)
    {
        if (data[offset + 2] != 0 || data[offset + 3] != 0)
        {
            throw new InvalidDataException(
                $"SKA platform: bone {bone} high-resolution {kind} key {key} has nonzero alignment padding");
        }
    }

    private static int Align4(int value) => checked((value + 3) & ~3);
}
