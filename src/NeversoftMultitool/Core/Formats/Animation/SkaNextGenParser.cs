using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Project 8 / Proving Ground Xbox 360 and PS3 SKA reader. These files wrap
///     a big-endian THAW-family payload in a 0x20-byte descriptor. The payload
///     uses four absolute file offsets for Q data, T data, Q byte sizes and T
///     byte sizes instead of serializing the streams consecutively.
/// </summary>
internal static class SkaNextGenParser
{
    private const int WrapperSize = 0x20;
    private const uint MissingOffset = uint.MaxValue;
    private const int P8HeaderSize = 0x28;
    private const int PgHeaderSize = 0x48;

    private const uint FlagHiResTimestamps = 1u << 8;
    private const uint FlagCompactQKeys = 1u << 15;
    private const uint FlagSingleFrameVectors = 1u << 6;

    internal static bool IsNextGenSka(ReadOnlySpan<byte> data) =>
        TryReadLayout(data, out _);

    internal static bool TryProbe(ReadOnlySpan<byte> data, out SkaProbeResult? probe)
    {
        probe = null;
        if (!TryReadLayout(data, out var layout))
            return false;

        probe = new SkaProbeResult(layout.Duration, layout.NumBones);
        return true;
    }

    internal static SkaAnimation Parse(ReadOnlySpan<byte> data, SkaCompressTable? compressTable)
    {
        if (!TryReadLayout(data, out var layout))
            throw new InvalidDataException("Next-gen SKA: invalid wrapper, header, or section table");

        var qSizes = ReadTrackSizes(data, layout.QSizesOffset, layout.NumBones);
        var tSizes = ReadTrackSizes(data, layout.TSizesOffset, layout.NumBones);

        var tracks = (layout.Flags & SkaFile.FlagUseCompressTable) != 0
            ? ParseCompressedTracks(data, layout, qSizes, tSizes, compressTable)
            : ParsePlatformTracks(data, layout, qSizes, tSizes);

        var customKeys = ParseTail(data, layout, tracks);
        return new SkaAnimation
        {
            Version = (uint)layout.HeaderSize,
            Flags = layout.Flags,
            Duration = layout.Duration,
            BoneTracks = tracks,
            CustomKeys = customKeys,
            IsNextGenWrappedFormat = true
        };
    }

    private static SkaBoneTrack[] ParseCompressedTracks(
        ReadOnlySpan<byte> data,
        Layout layout,
        int[] qSizes,
        int[] tSizes,
        SkaCompressTable? compressTable)
    {
        var tracks = new SkaBoneTrack[layout.NumBones];
        var qOffset = layout.QDataOffset;
        var tOffset = layout.TDataOffset;
        var qCount = 0;
        var tCount = 0;

        for (var bone = 0; bone < tracks.Length; bone++)
        {
            var qEnd = checked(qOffset + qSizes[bone]);
            var tEnd = checked(tOffset + tSizes[bone]);
            var rotations = DecodeCompressedQTrack(
                data, ref qOffset, qEnd, layout.HeaderSize == PgHeaderSize,
                layout.Flags, compressTable);
            var translations = DecodeCompressedTTrack(
                data, ref tOffset, tEnd, layout.HeaderSize, compressTable);

            qCount = checked(qCount + rotations.Length);
            tCount = checked(tCount + translations.Length);
            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                RotationKeys = rotations,
                TranslationKeys = translations
            };
        }

        // The container stores these totals as u16. One shipped P8 mocap
        // bank has 66,790 Q keys (and likewise crosses the T-key limit), so
        // the on-disk declaration necessarily contains the low 16 bits.
        if ((qCount & ushort.MaxValue) != layout.NumQKeys)
        {
            throw new InvalidDataException(
                $"Next-gen SKA compressed: decoded {qCount} Q keys, " +
                $"header declares low word {layout.NumQKeys}");
        }

        if ((tCount & ushort.MaxValue) != layout.NumTKeys)
        {
            throw new InvalidDataException(
                $"Next-gen SKA compressed: decoded {tCount} T keys, " +
                $"header declares low word {layout.NumTKeys}");
        }

        return tracks;
    }

    private static SkaRotationKey[] DecodeCompressedQTrack(
        ReadOnlySpan<byte> data,
        ref int offset,
        int end,
        bool alignComponents,
        uint flags,
        SkaCompressTable? table)
    {
        ValidateTrackRange(data.Length, offset, end, "Q");
        var keys = new List<SkaRotationKey>();
        var hiResTimestamp = (flags & FlagHiResTimestamps) != 0;
        var compact = (flags & FlagCompactQKeys) != 0;

        while (offset < end)
        {
            var recordStart = offset;
            int timestamp = 0;
            if (hiResTimestamp)
            {
                EnsureTrackBytes(offset, end, 2, "Q high-resolution timestamp");
                timestamp = ReadU16(data, offset) & 0x7FFF;
                offset += 2;
            }

            EnsureTrackBytes(offset, end, 2, "Q key header");
            var header = ReadU16(data, offset);
            var sign = (header & 0x8000) != 0;
            offset += 2;

            float x;
            float y;
            float z;
            if ((header & 0x4000) == 0)
            {
                if (!hiResTimestamp)
                    timestamp = header & 0x7FFF;

                if (compact)
                {
                    EnsureTrackBytes(offset, end, 3, "Q direct compact payload");
                    x = (short)(data[offset] << 8) / 16384f;
                    y = (short)(data[offset + 1] << 8) / 16384f;
                    z = (short)(data[offset + 2] << 8) / 16384f;
                    offset += 3;
                }
                else
                {
                    EnsureTrackBytes(offset, end, 6, "Q direct payload");
                    x = ReadS16(data, offset) / 16384f;
                    y = ReadS16(data, offset + 2) / 16384f;
                    z = ReadS16(data, offset + 4) / 16384f;
                    offset += 6;
                }
            }
            else if ((header & 0x3800) == 0)
            {
                if (!hiResTimestamp)
                    timestamp = header & 0x07FF;

                EnsureTrackBytes(offset, end, 1, "Q lookup index");
                var index = data[offset++];
                if (table == null)
                {
                    throw new InvalidDataException(
                        $"Next-gen SKA compressed Q lookup index {index} requires a Q48 compression table");
                }

                x = table.Q48[index].X / 16384f;
                y = table.Q48[index].Y / 16384f;
                z = table.Q48[index].Z / 16384f;
            }
            else
            {
                if (!hiResTimestamp)
                    timestamp = header & 0x07FF;

                x = ReadQComponent(
                    data, ref offset, end, (header & 0x2000) != 0,
                    compact, alignComponents, table);
                y = ReadQComponent(
                    data, ref offset, end, (header & 0x1000) != 0,
                    compact, alignComponents, table);
                z = ReadQComponent(
                    data, ref offset, end, (header & 0x0800) != 0,
                    compact, alignComponents, table);
            }

            if (alignComponents && offset < end)
            {
                // PG keeps every 16-bit field naturally aligned and rounds a
                // record ending in an 8-bit field to two bytes. These skipped
                // allocation bytes are not initialized consistently, so their
                // values are deliberately not interpreted.
                var aligned = checked((offset + 1) & ~1);
                if (aligned > end)
                {
                    throw new InvalidDataException(
                        $"Next-gen SKA Q key at 0x{recordStart:X} crosses its aligned track boundary 0x{end:X}");
                }

                offset = aligned;
            }

            keys.Add(new SkaRotationKey(
                timestamp / 60f,
                SkaFile.ReconstructQuat(x, y, z, sign)));
        }

        if (offset != end)
            throw new InvalidDataException("Next-gen SKA Q track did not consume its size-table entry");
        return keys.ToArray();
    }

    private static float ReadQComponent(
        ReadOnlySpan<byte> data,
        ref int offset,
        int end,
        bool narrow,
        bool compact,
        bool alignFullComponents,
        SkaCompressTable? table)
    {
        if (narrow)
        {
            EnsureTrackBytes(offset, end, 1, "Q narrow component");
            var index = data[offset++];
            if (table == null)
            {
                throw new InvalidDataException(
                    $"Next-gen SKA compressed Q component index {index} requires a Q48 compression table");
            }

            return table.Q48[index].Scalar / 16384f;
        }

        if (compact)
        {
            EnsureTrackBytes(offset, end, 1, "Q compact component");
            return (short)(data[offset++] << 8) / 16384f;
        }

        if (alignFullComponents && (offset & 1) != 0)
        {
            EnsureTrackBytes(offset, end, 1, "Q full-component alignment");
            offset++;
        }

        EnsureTrackBytes(offset, end, 2, "Q full component");
        var value = ReadS16(data, offset);
        offset += 2;
        return value / 16384f;
    }

    private static SkaTranslationKey[] DecodeCompressedTTrack(
        ReadOnlySpan<byte> data,
        ref int offset,
        int end,
        int headerSize,
        SkaCompressTable? table)
    {
        ValidateTrackRange(data.Length, offset, end, "T");
        if (headerSize == PgHeaderSize)
            return DecodePgTTrack(data, ref offset, end);

        var keys = new List<SkaTranslationKey>();

        while (offset < end)
        {
            EnsureTrackBytes(offset, end, 1, "T key header");
            var flag = data[offset];
            int timestamp;
            offset++;
            if ((flag & 0x40) != 0)
            {
                timestamp = flag & 0x3F;
            }
            else
            {
                EnsureTrackBytes(offset, end, 2, "T full timestamp");
                timestamp = ReadU16(data, offset);
                offset += 2;
            }

            Vector3 translation;
            if ((flag & 0x80) != 0)
            {
                EnsureTrackBytes(offset, end, 1, "T lookup index");
                var index = data[offset++];
                if (table == null)
                {
                    throw new InvalidDataException(
                        $"Next-gen SKA compressed T lookup index {index} requires a T48 compression table");
                }

                translation = new Vector3(
                    table.T48[index].X / 32f,
                    table.T48[index].Y / 32f,
                    table.T48[index].Z / 32f);
            }
            else
            {
                EnsureTrackBytes(offset, end, 12, "T float payload");
                var x = ReadF32(data, offset);
                var y = ReadF32(data, offset + 4);
                var z = ReadF32(data, offset + 8);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new InvalidDataException("Next-gen SKA T key contains a non-finite component");
                translation = new Vector3(x, y, z);
                offset += 12;
            }

            keys.Add(new SkaTranslationKey(timestamp / 60f, translation));
        }

        if (offset != end)
            throw new InvalidDataException("Next-gen SKA T track did not consume its size-table entry");
        return keys.ToArray();
    }

    private static SkaTranslationKey[] DecodePgTTrack(
        ReadOnlySpan<byte> data,
        ref int offset,
        int end)
    {
        // PG replaced the earlier variable-width T stream with 16-byte
        // records: a four-byte time header followed by three big-endian
        // floats. Some tracks start with the fixed 12-byte marker (2, 0, 0),
        // which is excluded from the header's key count. Across both retail
        // platforms, every T size is therefore 0 or 12 modulo 16.
        var byteCount = end - offset;
        var prefixSize = byteCount & 15;
        if (prefixSize is not (0 or 12))
        {
            throw new InvalidDataException(
                $"Next-gen PG SKA T track has invalid byte size {byteCount}");
        }

        if (prefixSize != 0)
        {
            if (ReadU32(data, offset) != 0x40000000 ||
                ReadU32(data, offset + 4) != 0 ||
                ReadU32(data, offset + 8) != 0)
            {
                throw new InvalidDataException(
                    "Next-gen PG SKA T track has an invalid 12-byte prefix marker");
            }

            offset += prefixSize;
        }

        var keys = new SkaTranslationKey[(end - offset) / 16];
        for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++, offset += 16)
        {
            EnsureTrackBytes(offset, end, 16, "PG T key");
            var flag = data[offset];
            if ((flag & 0x80) != 0)
            {
                throw new InvalidDataException(
                    $"Next-gen PG SKA T key at 0x{offset:X} uses an unsupported lookup flag");
            }

            var timestamp = (flag & 0x40) != 0
                ? flag & 0x3F
                : ReadU16(data, offset + 1);
            var x = ReadF32(data, offset + 4);
            var y = ReadF32(data, offset + 8);
            var z = ReadF32(data, offset + 12);
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                throw new InvalidDataException("Next-gen PG SKA T key contains a non-finite component");
            keys[keyIndex] = new SkaTranslationKey(timestamp / 60f, new Vector3(x, y, z));
        }

        return keys;
    }

    private static SkaBoneTrack[] ParsePlatformTracks(
        ReadOnlySpan<byte> data,
        Layout layout,
        int[] qSizes,
        int[] tSizes)
    {
        if ((layout.Flags & FlagSingleFrameVectors) != 0)
            return ParseSingleFramePlatformTracks(data, layout, qSizes, tSizes);

        var tracks = new SkaBoneTrack[layout.NumBones];
        var qOffset = layout.QDataOffset;
        var tOffset = layout.TDataOffset;
        var qCount = 0;
        var tCount = 0;

        for (var bone = 0; bone < tracks.Length; bone++)
        {
            if ((qSizes[bone] & 15) != 0 || (tSizes[bone] & 15) != 0)
            {
                throw new InvalidDataException(
                    $"Next-gen SKA platform: bone {bone} Q/T byte sizes are not multiples of 16");
            }

            var rotations = new SkaRotationKey[qSizes[bone] / 16];
            for (var keyIndex = 0; keyIndex < rotations.Length; keyIndex++, qOffset += 16)
            {
                var timestamp = ReadU16(data, qOffset);
                var x = ReadF32(data, qOffset + 4);
                var y = ReadF32(data, qOffset + 8);
                var z = ReadF32(data, qOffset + 12);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new InvalidDataException("Next-gen SKA platform Q key contains a non-finite component");
                rotations[keyIndex] = new SkaRotationKey(
                    (timestamp & 0x7FFF) / 60f,
                    SkaFile.ReconstructQuat(x, y, z, (timestamp & 0x8000) != 0));
            }

            var translations = new SkaTranslationKey[tSizes[bone] / 16];
            for (var keyIndex = 0; keyIndex < translations.Length; keyIndex++, tOffset += 16)
            {
                var timestamp = ReadU16(data, tOffset);
                var x = ReadF32(data, tOffset + 4);
                var y = ReadF32(data, tOffset + 8);
                var z = ReadF32(data, tOffset + 12);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new InvalidDataException("Next-gen SKA platform T key contains a non-finite component");
                translations[keyIndex] = new SkaTranslationKey(
                    (timestamp & 0x7FFF) / 60f,
                    new Vector3(x, y, z));
            }

            qCount = checked(qCount + rotations.Length);
            tCount = checked(tCount + translations.Length);
            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                RotationKeys = rotations,
                TranslationKeys = translations
            };
        }

        if (qCount != layout.NumQKeys || tCount != layout.NumTKeys)
        {
            throw new InvalidDataException(
                $"Next-gen SKA platform: decoded {qCount}Q/{tCount}T keys, " +
                $"header declares {layout.NumQKeys}Q/{layout.NumTKeys}T");
        }

        return tracks;
    }

    private static SkaBoneTrack[] ParseSingleFramePlatformTracks(
        ReadOnlySpan<byte> data,
        Layout layout,
        int[] qSizes,
        int[] tSizes)
    {
        // Bit 6 selects PG/P8's neutral-pose stream: exactly one full
        // big-endian Quaternion and homogeneous Vector4 per bone, with no
        // serialized timestamps. It is structurally distinct from aligned
        // platform animation keys even though both records are 16 bytes.
        if (layout.NumQKeys != layout.NumBones || layout.NumTKeys != layout.NumBones)
        {
            throw new InvalidDataException(
                "Next-gen SKA single-frame key totals do not match the bone count");
        }

        var tracks = new SkaBoneTrack[layout.NumBones];
        var qOffset = layout.QDataOffset;
        var tOffset = layout.TDataOffset;
        for (var bone = 0; bone < tracks.Length; bone++, qOffset += 16, tOffset += 16)
        {
            if (qSizes[bone] != 16 || tSizes[bone] != 16)
            {
                throw new InvalidDataException(
                    $"Next-gen SKA single-frame bone {bone} does not have one Q/T Vector4");
            }

            var qx = ReadF32(data, qOffset);
            var qy = ReadF32(data, qOffset + 4);
            var qz = ReadF32(data, qOffset + 8);
            var qw = ReadF32(data, qOffset + 12);
            var tx = ReadF32(data, tOffset);
            var ty = ReadF32(data, tOffset + 4);
            var tz = ReadF32(data, tOffset + 8);
            var tw = ReadF32(data, tOffset + 12);
            if (!float.IsFinite(qx) || !float.IsFinite(qy) ||
                !float.IsFinite(qz) || !float.IsFinite(qw) ||
                !float.IsFinite(tx) || !float.IsFinite(ty) ||
                !float.IsFinite(tz) || !float.IsFinite(tw))
            {
                throw new InvalidDataException(
                    $"Next-gen SKA single-frame bone {bone} contains a non-finite component");
            }

            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                RotationKeys =
                [
                    new SkaRotationKey(
                        0,
                        Quaternion.Conjugate(new Quaternion(qx, qy, qz, qw)))
                ],
                TranslationKeys =
                [
                    new SkaTranslationKey(0, new Vector3(tx, ty, tz))
                ]
            };
        }

        return tracks;
    }

    private static SkaCustomKey[] ParseTail(
        ReadOnlySpan<byte> data,
        Layout layout,
        SkaBoneTrack[] tracks)
    {
        var offset = checked(layout.TSizesOffset + 2 * layout.NumBones);
        offset = AlignAndRequireZero(data, offset, 4, "T size-table alignment");

        if ((layout.Flags & SkaFile.FlagObjectAnimData) != 0)
        {
            var namesEnd = (long)offset + 4L * layout.NumBones;
            if (namesEnd > data.Length)
                throw new InvalidDataException("Next-gen SKA bone-name table overruns file");

            for (var bone = 0; bone < tracks.Length; bone++, offset += 4)
            {
                var old = tracks[bone];
                tracks[bone] = new SkaBoneTrack
                {
                    BoneIndex = old.BoneIndex,
                    RotationKeys = old.RotationKeys,
                    TranslationKeys = old.TranslationKeys,
                    BoneNameChecksum = ReadU32(data, offset)
                };
            }
        }

        if ((layout.Flags & SkaFile.FlagPartialAnim) != 0)
        {
            offset = AlignAndRequireZero(data, offset, 4, "partial-animation alignment");
            EnsureFileBytes(data.Length, offset, 4, "partial-animation bone count");
            var originalBoneCount = ReadU32(data, offset);
            if (originalBoneCount == 0 || originalBoneCount > 4096)
                throw new InvalidDataException(
                    $"Next-gen SKA partial animation has implausible original bone count {originalBoneCount}");
            offset += 4;

            var maskWords = checked((int)((originalBoneCount + 31) / 32));
            EnsureFileBytes(data.Length, offset, checked(maskWords * 4), "partial-animation mask");
            offset += maskWords * 4;
        }

        offset = AlignAndRequireZero(data, offset, 4, "custom-key alignment");
        if (layout.NumCustomKeys > 0 && offset != layout.CustomDataOffset)
        {
            throw new InvalidDataException(
                $"Next-gen SKA custom-key pointer is 0x{layout.CustomDataOffset:X}, " +
                $"derived tail starts at 0x{offset:X}");
        }

        var keys = new SkaCustomKey[layout.NumCustomKeys];
        for (var i = 0; i < keys.Length; i++)
        {
            EnsureFileBytes(data.Length, offset, 12, $"custom key {i} header");
            var timestamp = ReadU32(data, offset);
            var type = ReadU32(data, offset + 4);
            var size = ReadU32(data, offset + 8);
            if (size < 12 || (size & 3) != 0 || size > int.MaxValue)
                throw new InvalidDataException($"Next-gen SKA custom key {i} has invalid size {size}");
            EnsureFileBytes(data.Length, offset, (int)size, $"custom key {i}");
            if ((type == 1 || type == 4) && size != 16)
            {
                throw new InvalidDataException(
                    $"Next-gen SKA custom key {i}: decoded type {type} must be 16 bytes");
            }

            var payloadLength = (int)size - 12;
            var payload = data.Slice(offset + 12, payloadLength).ToArray();
            keys[i] = new SkaCustomKey
            {
                Timestamp = timestamp,
                Type = type,
                Size = size,
                Payload = payload,
                Fov = type == 1 ? ReadF32(data, offset + 12) : null,
                ScriptQbKey = type == 4 ? ReadU32(data, offset + 12) : null
            };
            offset += (int)size;
        }

        if (offset != data.Length)
        {
            throw new InvalidDataException(
                $"Next-gen SKA tail ends at 0x{offset:X}, file ends at 0x{data.Length:X}");
        }

        return keys;
    }

    private static int[] ReadTrackSizes(ReadOnlySpan<byte> data, int offset, int count)
    {
        var sizes = new int[count];
        for (var i = 0; i < sizes.Length; i++)
            sizes[i] = ReadU16(data, offset + 2 * i);
        return sizes;
    }

    private static bool TryReadLayout(ReadOnlySpan<byte> data, out Layout layout)
    {
        layout = default;
        if (data.Length < WrapperSize + P8HeaderSize)
            return false;

        if (ReadU32(data, 0) != 0 ||
            ReadU32(data, 4) != MissingOffset ||
            ReadU32(data, 8) != (uint)data.Length ||
            ReadU32(data, 12) != WrapperSize)
        {
            return false;
        }

        // The final four wrapper fields are optional absolute pointers. Their
        // roles vary with flags (bone names, partial mask, and related runtime
        // metadata), but every live value is aligned and file-bounded.
        for (var offset = 0x10; offset < WrapperSize; offset += 4)
        {
            var pointer = ReadU32(data, offset);
            if (pointer != MissingOffset &&
                ((pointer & 3) != 0 || pointer < WrapperSize || pointer >= data.Length))
            {
                return false;
            }
        }

        var headerSize = (int)ReadU32(data, WrapperSize);
        if (headerSize is not (P8HeaderSize or PgHeaderSize) ||
            WrapperSize + headerSize > data.Length)
        {
            return false;
        }

        if (headerSize == PgHeaderSize)
        {
            // PG inserted two four-float transform vectors here. Ordinary
            // character clips use (1,0,0,0) twice, while platform/object
            // animations carry authored values; all corpus values are finite.
            for (var i = 0; i < 8; i++)
            {
                if (!float.IsFinite(ReadF32(data, WrapperSize + 0x10 + 4 * i)))
                    return false;
            }
        }

        if (data[WrapperSize + 0x0C] != 0)
        {
            return false;
        }

        var flags = ReadU32(data, WrapperSize + 4);
        var formatFlags = flags & (SkaFile.FlagUseCompressTable | SkaFile.FlagPlatform);
        if (formatFlags == 0 ||
            formatFlags == (SkaFile.FlagUseCompressTable | SkaFile.FlagPlatform))
        {
            return false;
        }

        var duration = ReadF32(data, WrapperSize + 8);
        if (!float.IsFinite(duration) || duration < 0)
            return false;

        var numBones = data[WrapperSize + 0x0D];
        if (numBones == 0)
            return false;
        var numQKeys = ReadU16(data, WrapperSize + 0x0E);
        var countTail = WrapperSize + headerSize - 0x18;
        var numTKeys = ReadU16(data, countTail);
        var numCustomKeys = ReadU16(data, countTail + 2);
        var customDataWord = ReadU32(data, countTail + 4);
        var customDataOffset = -1;
        if (numCustomKeys == 0)
        {
            if (customDataWord != MissingOffset)
                return false;
        }
        else
        {
            customDataOffset = customDataWord <= int.MaxValue ? (int)customDataWord : -1;
            if (customDataOffset < WrapperSize + headerSize ||
                customDataOffset >= data.Length ||
                (customDataOffset & 3) != 0)
            {
                return false;
            }
        }

        var tableOffset = WrapperSize + headerSize - 0x10;
        var qDataOffset = ReadOffset(data, tableOffset);
        var tDataOffset = ReadOffset(data, tableOffset + 4);
        var qSizesOffset = ReadOffset(data, tableOffset + 8);
        var tSizesOffset = ReadOffset(data, tableOffset + 12);
        if (qDataOffset < WrapperSize + headerSize ||
            qDataOffset >= tDataOffset ||
            tDataOffset >= qSizesOffset ||
            qSizesOffset >= tSizesOffset ||
            tSizesOffset >= data.Length ||
            (qDataOffset & 3) != 0 ||
            (tDataOffset & 3) != 0 ||
            (qSizesOffset & 3) != 0 ||
            (tSizesOffset & 3) != 0)
        {
            return false;
        }

        if ((long)qSizesOffset + 2L * numBones > tSizesOffset ||
            (long)tSizesOffset + 2L * numBones > data.Length)
        {
            return false;
        }

        long qBytes = 0;
        long tBytes = 0;
        for (var bone = 0; bone < numBones; bone++)
        {
            qBytes += ReadU16(data, qSizesOffset + 2 * bone);
            tBytes += ReadU16(data, tSizesOffset + 2 * bone);
        }

        if (qBytes > tDataOffset - qDataOffset || tBytes > qSizesOffset - tDataOffset)
            return false;

        if (!IsAllZero(data.Slice(qDataOffset + (int)qBytes, tDataOffset - qDataOffset - (int)qBytes)) ||
            !IsAllZero(data.Slice(tDataOffset + (int)tBytes, qSizesOffset - tDataOffset - (int)tBytes)) ||
            !IsAllZero(data.Slice(qSizesOffset + 2 * numBones, tSizesOffset - qSizesOffset - 2 * numBones)))
        {
            return false;
        }

        layout = new Layout(
            headerSize,
            flags,
            duration,
            numBones,
            numQKeys,
            numTKeys,
            numCustomKeys,
            customDataOffset,
            qDataOffset,
            tDataOffset,
            qSizesOffset,
            tSizesOffset);
        return true;
    }

    private static int ReadOffset(ReadOnlySpan<byte> data, int offset)
    {
        var value = ReadU32(data, offset);
        return value <= int.MaxValue ? (int)value : -1;
    }

    private static int AlignAndRequireZero(
        ReadOnlySpan<byte> data, int offset, int alignment, string context)
    {
        var aligned = checked((offset + alignment - 1) & ~(alignment - 1));
        if (aligned > data.Length)
            throw new InvalidDataException($"Next-gen SKA {context} overruns file");
        RequireZeroBytes(data, offset, aligned, context);
        return aligned;
    }

    private static void RequireZeroBytes(
        ReadOnlySpan<byte> data, int start, int end, string context)
    {
        if (start < 0 || end < start || end > data.Length || !IsAllZero(data[start..end]))
            throw new InvalidDataException($"Next-gen SKA {context} is nonzero or out of bounds");
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
            if (value != 0)
                return false;
        return true;
    }

    private static void ValidateTrackRange(int dataLength, int offset, int end, string kind)
    {
        if (offset < 0 || end < offset || end > dataLength)
        {
            throw new InvalidDataException(
                $"Next-gen SKA {kind} track range 0x{offset:X}..0x{end:X} is outside file");
        }
    }

    private static void EnsureTrackBytes(int offset, int end, int count, string context)
    {
        if (offset < 0 || count < 0 || end < offset || count > end - offset)
        {
            throw new InvalidDataException(
                $"Next-gen SKA {context} at 0x{offset:X} crosses track boundary 0x{end:X}");
        }
    }

    private static void EnsureFileBytes(int dataLength, int offset, int count, string context)
    {
        if (offset < 0 || count < 0 || offset > dataLength - count)
            throw new InvalidDataException($"Next-gen SKA {context} overruns file");
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static short ReadS16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt16BigEndian(data[offset..]);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static float ReadF32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadSingleBigEndian(data[offset..]);

    private readonly record struct Layout(
        int HeaderSize,
        uint Flags,
        float Duration,
        int NumBones,
        int NumQKeys,
        int NumTKeys,
        int NumCustomKeys,
        int CustomDataOffset,
        int QDataOffset,
        int TDataOffset,
        int QSizesOffset,
        int TSizesOffset);
}
