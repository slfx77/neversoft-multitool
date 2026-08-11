using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Parser for the version 2/3 INTERMEDIATE SKA members in THUG's bare
///     authoring <c>.cut</c> libraries. These are little-endian, full-float
///     animation streams paired one-for-one with the compiled SKAs in the
///     corresponding <c>.cut.ps2</c> libraries.
/// </summary>
internal static class SkaIntermediateParser
{
    private const int HeaderSize = 32;
    private const int EmbeddedSkeletonHeaderSize = 8;
    private const int RotationRecordSize = 20;
    private const int TranslationRecordSize = 16;
    private const int MaxBones = 256;
    private const float FramesPerSecond = 60f;
    private const uint FlagPreRotatedRoot = 1u << 25;
    private const uint FlagCutsceneData = 1u << 20;
    private const uint KnownFlagsMask = SkaFile.FlagIntermediate |
                                        SkaFile.FlagUncompressed |
                                        SkaFile.FlagCompressedTime |
                                        FlagPreRotatedRoot |
                                        FlagCutsceneData;
    // Full-corpus oracle (4,588,265 keys): quaternion norm ranges from
    // 0.9999797902 to 1.0000175845 (max |norm-1| 2.021e-5), equivalent to
    // a maximum observed |normSquared-1| of about 4.042e-5.
    private const double MaxQuaternionNormSquaredError = 1e-4;
    // The largest corpus timestamp overshoots duration*60 only through float
    // rounding, by 0.0001526 of a frame.
    private const double MaxDurationFrameRoundingError = 1e-3;

    internal static bool IsIntermediateSka(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + EmbeddedSkeletonHeaderSize)
            return false;

        try
        {
            return ReadLayout(data).EndOffset == data.Length;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static SkaProbeResult? TryProbe(ReadOnlySpan<byte> data)
    {
        if (!IsIntermediateSka(data))
            return null;

        var duration = BinaryPrimitives.ReadSingleLittleEndian(data[8..]);
        var boneCount = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        return new SkaProbeResult(duration, checked((int)boneCount));
    }

    internal static SkaAnimation Parse(ReadOnlySpan<byte> data)
    {
        var layout = ReadLayout(data);

        var boneNames = ReadUInt32Array(data, layout.BoneNamesOffset, layout.BoneCount);
        var parentNames = ReadUInt32Array(data, layout.ParentNamesOffset, layout.BoneCount);
        var flipNames = ReadUInt32Array(data, layout.FlipNamesOffset, layout.BoneCount);
        var qCounts = ReadCountArray(
            data, layout.RotationCountsOffset, layout.BoneCount,
            layout.RotationKeyCount, "rotation");
        var tCounts = ReadCountArray(
            data, layout.TranslationCountsOffset, layout.BoneCount,
            layout.TranslationKeyCount, "translation");

        var tracks = new SkaBoneTrack[layout.BoneCount];
        var rotationFrames = new uint[layout.BoneCount][];
        var translationFrames = new uint[layout.BoneCount][];
        var sourceRotations = new Vector4[layout.BoneCount][];
        var qOffset = layout.RotationDataOffset;
        var tOffset = layout.TranslationDataOffset;

        for (var bone = 0; bone < layout.BoneCount; bone++)
        {
            var qKeys = new SkaRotationKey[qCounts[bone]];
            var qFrameValues = new uint[qKeys.Length];
            var sourceQValues = new Vector4[qKeys.Length];
            uint previousFrame = 0;
            for (var key = 0; key < qKeys.Length; key++)
            {
                var frame = BinaryPrimitives.ReadUInt32LittleEndian(data[qOffset..]);
                if (key > 0 && frame <= previousFrame)
                    throw new InvalidDataException(
                        $"SKA intermediate: bone {bone} rotation frame {frame} is not greater than {previousFrame}");
                ValidateDurationFrame(frame, layout.Duration, bone, key, "rotation");

                var qx = BinaryPrimitives.ReadSingleLittleEndian(data[(qOffset + 4)..]);
                var qy = BinaryPrimitives.ReadSingleLittleEndian(data[(qOffset + 8)..]);
                var qz = BinaryPrimitives.ReadSingleLittleEndian(data[(qOffset + 12)..]);
                var qw = BinaryPrimitives.ReadSingleLittleEndian(data[(qOffset + 16)..]);
                if (!float.IsFinite(qx) || !float.IsFinite(qy) ||
                    !float.IsFinite(qz) || !float.IsFinite(qw))
                {
                    throw new InvalidDataException(
                        $"SKA intermediate: bone {bone} rotation key {key} is non-finite");
                }

                var normSquared = (double)qx * qx + (double)qy * qy +
                                  (double)qz * qz + (double)qw * qw;
                if (Math.Abs(normSquared - 1d) > MaxQuaternionNormSquaredError)
                    throw new InvalidDataException(
                        $"SKA intermediate: bone {bone} rotation key {key} has " +
                        $"non-unit squared length {normSquared:R}");

                // QuatVecToMatrix consumes the conjugate throughout the THUG
                // animation/skeleton path; keep the ordinary SKA IR convention.
                var rotation = Quaternion.Conjugate(new Quaternion(qx, qy, qz, qw));
                qKeys[key] = new SkaRotationKey(frame / FramesPerSecond, rotation);
                qFrameValues[key] = frame;
                sourceQValues[key] = new Vector4(qx, qy, qz, qw);
                previousFrame = frame;
                qOffset += RotationRecordSize;
            }

            var tKeys = new SkaTranslationKey[tCounts[bone]];
            var tFrameValues = new uint[tKeys.Length];
            previousFrame = 0;
            for (var key = 0; key < tKeys.Length; key++)
            {
                var frame = BinaryPrimitives.ReadUInt32LittleEndian(data[tOffset..]);
                if (key > 0 && frame <= previousFrame)
                    throw new InvalidDataException(
                        $"SKA intermediate: bone {bone} translation frame {frame} is not greater than {previousFrame}");
                ValidateDurationFrame(frame, layout.Duration, bone, key, "translation");

                var x = BinaryPrimitives.ReadSingleLittleEndian(data[(tOffset + 4)..]);
                var y = BinaryPrimitives.ReadSingleLittleEndian(data[(tOffset + 8)..]);
                var z = BinaryPrimitives.ReadSingleLittleEndian(data[(tOffset + 12)..]);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new InvalidDataException(
                        $"SKA intermediate: bone {bone} translation key {key} is non-finite");

                tKeys[key] = new SkaTranslationKey(frame / FramesPerSecond, new Vector3(x, y, z));
                tFrameValues[key] = frame;
                previousFrame = frame;
                tOffset += TranslationRecordSize;
            }

            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                BoneNameChecksum = boneNames[bone],
                RotationKeys = qKeys,
                TranslationKeys = tKeys
            };
            rotationFrames[bone] = qFrameValues;
            translationFrames[bone] = tFrameValues;
            sourceRotations[bone] = sourceQValues;
        }

        if (qOffset != layout.TranslationCountsOffset || tOffset != data.Length)
            throw new InvalidDataException(
                $"SKA intermediate: parser ended at Q=0x{qOffset:X}, T=0x{tOffset:X}; " +
                $"expected Q=0x{layout.TranslationCountsOffset:X}, EOF=0x{data.Length:X}");

        return new SkaAnimation
        {
            Version = layout.Version,
            Flags = layout.Flags,
            Duration = layout.Duration,
            BoneTracks = tracks,
            IntermediateMetadata = new SkaIntermediateMetadata
            {
                SkeletonChecksum = layout.SkeletonChecksum,
                BoneNameChecksums = boneNames,
                ParentNameChecksums = parentNames,
                FlipNameChecksums = flipNames,
                RotationFrames = rotationFrames,
                TranslationFrames = translationFrames,
                SourceRotations = sourceRotations
            }
        };
    }

    private static Layout ReadLayout(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + EmbeddedSkeletonHeaderSize)
            throw new InvalidDataException(
                $"SKA intermediate: file is {data.Length} bytes, smaller than the 40-byte fixed prefix");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version is not (2 or 3))
            throw new InvalidDataException(
                $"SKA intermediate: version {version} is not supported (expected 2 or 3)");

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if ((flags & SkaFile.FlagIntermediate) == 0)
            throw new InvalidDataException("SKA intermediate: INTERMEDIATE flag is not set");
        if ((flags & ~KnownFlagsMask) != 0)
            throw new InvalidDataException(
                $"SKA intermediate: flags 0x{flags:X8} contain unsupported bits 0x{flags & ~KnownFlagsMask:X8}");
        if ((flags & SkaFile.FlagCompressedTime) == 0)
            throw new InvalidDataException(
                "SKA intermediate: timestamp unit is unproven without COMPRESSEDTIME");

        var duration = BinaryPrimitives.ReadSingleLittleEndian(data[8..]);
        if (!float.IsFinite(duration) || duration < 0f)
            throw new InvalidDataException(
                $"SKA intermediate: invalid duration {duration}");
        var skeletonChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var boneCount = ReadBoundedCount(data, 16, MaxBones, "bone");
        var qCount = ReadBoundedCount(data, 20, data.Length / RotationRecordSize, "rotation key");
        var tCount = ReadBoundedCount(data, 24, data.Length / TranslationRecordSize, "translation key");
        var customCount = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        if (customCount != 0)
            throw new InvalidDataException(
                $"SKA intermediate: {customCount} custom keys are present; their authoring grammar is unsupported");

        var embeddedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        var embeddedBoneCount = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        if (embeddedChecksum != skeletonChecksum)
            throw new InvalidDataException(
                $"SKA intermediate: embedded skeleton checksum 0x{embeddedChecksum:X8} " +
                $"does not match header 0x{skeletonChecksum:X8}");
        if (embeddedBoneCount != (uint)boneCount)
            throw new InvalidDataException(
                $"SKA intermediate: embedded bone count {embeddedBoneCount} " +
                $"does not match header {boneCount}");

        long boneNamesOffset = HeaderSize + EmbeddedSkeletonHeaderSize;
        long parentNamesOffset = boneNamesOffset + checked(4L * boneCount);
        long flipNamesOffset = parentNamesOffset + checked(4L * boneCount);
        long qCountsOffset = flipNamesOffset + checked(4L * boneCount);
        long qDataOffset = qCountsOffset + checked(4L * boneCount);
        long tCountsOffset = qDataOffset + checked((long)RotationRecordSize * qCount);
        long tDataOffset = tCountsOffset + checked(4L * boneCount);
        long endOffset = tDataOffset + checked((long)TranslationRecordSize * tCount);
        if (endOffset > data.Length)
            throw new InvalidDataException(
                $"SKA intermediate: declared streams end at 0x{endOffset:X}, past file length 0x{data.Length:X}");

        if (endOffset != data.Length)
        {
            throw new InvalidDataException(
                $"SKA intermediate: streams end at 0x{endOffset:X}, not exact EOF 0x{data.Length:X}");
        }

        var layout = new Layout(
            version, flags, duration, skeletonChecksum, boneCount, qCount, tCount,
            checked((int)boneNamesOffset), checked((int)parentNamesOffset),
            checked((int)flipNamesOffset), checked((int)qCountsOffset),
            checked((int)qDataOffset), checked((int)tCountsOffset),
            checked((int)tDataOffset), checked((int)endOffset));

        // Count sums are part of the layout contract and are cheap enough for
        // detection/probing; this prevents a structurally sized random blob
        // from reaching the allocating parser.
        ValidateSkeletonTables(data, layout);
        ValidateCountSum(data, layout.RotationCountsOffset, boneCount, qCount, "rotation");
        ValidateCountSum(data, layout.TranslationCountsOffset, boneCount, tCount, "translation");
        return layout;
    }

    private static void ValidateSkeletonTables(ReadOnlySpan<byte> data, Layout layout)
    {
        var boneNames = ReadUInt32Array(data, layout.BoneNamesOffset, layout.BoneCount);
        var allNames = new HashSet<uint>(layout.BoneCount);
        for (var bone = 0; bone < boneNames.Length; bone++)
        {
            var name = boneNames[bone];
            if (name == 0)
                throw new InvalidDataException(
                    $"SKA intermediate: bone {bone} has a zero name checksum");
            if (!allNames.Add(name))
                throw new InvalidDataException(
                    $"SKA intermediate: bone {bone} repeats name checksum 0x{name:X8}");
        }

        var earlierNames = new HashSet<uint>(layout.BoneCount);
        var rootCount = 0;
        for (var bone = 0; bone < boneNames.Length; bone++)
        {
            var parent = BinaryPrimitives.ReadUInt32LittleEndian(
                data[(layout.ParentNamesOffset + bone * 4)..]);
            if (parent == 0)
            {
                rootCount++;
            }
            else if (!earlierNames.Contains(parent))
            {
                throw new InvalidDataException(
                    $"SKA intermediate: bone {bone} parent 0x{parent:X8} does not resolve to an earlier bone");
            }

            earlierNames.Add(boneNames[bone]);
        }

        if (rootCount != 1)
            throw new InvalidDataException(
                $"SKA intermediate: embedded skeleton has {rootCount} roots (expected exactly one)");

        for (var bone = 0; bone < boneNames.Length; bone++)
        {
            var flip = BinaryPrimitives.ReadUInt32LittleEndian(
                data[(layout.FlipNamesOffset + bone * 4)..]);
            if (flip != 0 && !allNames.Contains(flip))
                throw new InvalidDataException(
                    $"SKA intermediate: bone {bone} flip 0x{flip:X8} is not a listed bone");
            if (flip == boneNames[bone])
                throw new InvalidDataException(
                    $"SKA intermediate: bone {bone} refers to itself as its flip");
        }
    }

    private static int ReadBoundedCount(
        ReadOnlySpan<byte> data, int offset, int maximum, string label)
    {
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        if (raw > int.MaxValue || raw > (uint)maximum)
            throw new InvalidDataException(
                $"SKA intermediate: {label} count {raw} exceeds bound {maximum}");
        if (label == "bone" && raw == 0)
            throw new InvalidDataException("SKA intermediate: bone count is zero");
        return (int)raw;
    }

    private static uint[] ReadUInt32Array(ReadOnlySpan<byte> data, int offset, int count)
    {
        var values = new uint[count];
        for (var i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + i * 4)..]);
        return values;
    }

    private static int[] ReadCountArray(
        ReadOnlySpan<byte> data, int offset, int count, int expectedTotal, string label)
    {
        var values = new int[count];
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + i * 4)..]);
            if (raw > int.MaxValue)
                throw new InvalidDataException(
                    $"SKA intermediate: bone {i} {label} count {raw} exceeds Int32");
            values[i] = (int)raw;
            total = checked(total + raw);
        }

        if (total != expectedTotal)
            throw new InvalidDataException(
                $"SKA intermediate: per-bone {label} counts sum to {total}, header declares {expectedTotal}");
        return values;
    }

    private static void ValidateCountSum(
        ReadOnlySpan<byte> data, int offset, int count, int expectedTotal, string label)
    {
        long total = 0;
        for (var i = 0; i < count; i++)
            total = checked(total + BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + i * 4)..]));
        if (total != expectedTotal)
            throw new InvalidDataException(
                $"SKA intermediate: per-bone {label} counts sum to {total}, header declares {expectedTotal}");
    }

    private static void ValidateDurationFrame(
        uint frame, float duration, int bone, int key, string label)
    {
        var finalFrame = (double)duration * FramesPerSecond;
        if (frame > finalFrame + MaxDurationFrameRoundingError)
            throw new InvalidDataException(
                $"SKA intermediate: bone {bone} {label} key {key} frame {frame} " +
                $"exceeds duration endpoint {finalFrame:R}");
    }

    private readonly record struct Layout(
        uint Version,
        uint Flags,
        float Duration,
        uint SkeletonChecksum,
        int BoneCount,
        int RotationKeyCount,
        int TranslationKeyCount,
        int BoneNamesOffset,
        int ParentNamesOffset,
        int FlipNamesOffset,
        int RotationCountsOffset,
        int RotationDataOffset,
        int TranslationCountsOffset,
        int TranslationDataOffset,
        int EndOffset);
}
