using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     THAW-generation SKA v0x28 (PS2/PC/GC): compressed family with
///     byte-width scalar components and the hi-res cutscene family.
/// </summary>
internal static class SkaThawParser
{
    // THAW v0x28 container (THAW PS2/PC/GC + Project 8 + Proving Ground).
    // Little-endian on PS2/PC, big-endian on GC for the header, size tables,
    // partial-anim prelude, bone-name arrays and hi-res float keys; the
    // COMPRESSED key blobs and the standardkey compress tables ship as raw
    // little-endian byte streams on every platform (the GC port swaps at
    // runtime, same as THAW QB script bodies).
    //
    // Layout (validated on 7,057 PS2<->GC pairs; key grammar verified against
    // the THAW PS2 ELF readers before the research probes were retired):
    //   0x00 u32 version=0x28, u32 flags, f32 duration
    //   0x0C u8 zero, u8 numBones, u16 numQKeys, u16 numTKeys, u16 numCustomKeys
    //   0x14 u8[20] bone mask (all-0xFF in the shipped corpus)
    //   -- flags bit23 (USECOMPRESSTABLE) family, 91% of files:
    //   0x28 u32 qBytes, u32 tBytes
    //   0x30 u16 qSize[numBones], u16 tSize[numBones]
    //        [bit19: u32 origNumBones + u32 mask[ceil(orig/32)]]
    //        Q blobs (qBytes) then T blobs (tBytes), raw LE
    //   -- flags bit28 (PLATFORM) family (cutscene camera/object masters):
    //   0x28 [bit24: u32 boneNameQbKey[numBones]]
    //        [bit19: u32 origNumBones + u32 mask[ceil(orig/32)]]
    //        (u8 qCount, u8 tCount)[numBones], pad to 4
    //        Q keys: numQKeys x 16B (u16 ts15+sign, pad, f32 x/y/z)
    //        T keys: numTKeys x 16B (same shape)
    //        custom keys: {u32 timestamp, u32 type, u32 totalSize, payload}
    //
    // THAW Q-key grammar deltas vs THUG (engine-verified):
    //   bit16 (always set): byte-width components are indices into the scalar
    //         column (4th s16) of the standardkey Q table, not literal bytes.
    //   bit15: compact keys — every component is one byte: width-bit set ->
    //         scalar-table lookup, clear -> (byte << 8) as s16.
    //   bit8:  a u16 timestamp (15-bit) precedes each key's header u16, whose
    //         own timestamp bits are unused (sign stays in the header).
    // T keys are THUG-classic on all variants (global T table, /32 scale).
    private const uint FlagThawHiResTimestamps = 1u << 8;

    private const uint FlagThawCompactKeys = 1u << 15;
    // bit 16 = scalar-table byte-width components (always set in-corpus; the
    // decoders infer per-component width from the header, so it isn't gated on)

    internal const uint ThawVersion = 0x28;

    /// <summary>Detect the THAW v0x28 container in either endianness.</summary>
    internal static bool IsThawSka(ReadOnlySpan<byte> data, out bool bigEndian)
    {
        bigEndian = false;
        if (data.Length < 0x30)
            return false;

        var versionLe = BitConverter.ToUInt32(data);
        if (versionLe == ThawVersion << 24)
            bigEndian = true;
        else if (versionLe != ThawVersion)
            return false;

        // Belt-and-braces: byte@0x0C is always 0 and the 20-byte bone mask at
        // 0x14 is all-0xFF across the entire shipped corpus.
        if (data[0x0C] != 0)
            return false;
        for (var i = 0x14; i < 0x28; i++)
            if (data[i] != 0xFF)
                return false;

        var flags = new EndianSpanReader(data, bigEndian).U32(4);
        return (flags & (SkaFile.FlagUseCompressTable | SkaFile.FlagPlatform)) != 0;
    }

    internal static SkaAnimation ParseThaw(ReadOnlySpan<byte> data, bool bigEndian, SkaCompressTable? compressTable)
    {
        var r = new EndianSpanReader(data, bigEndian);
        var flags = r.U32(4);
        var duration = r.F32(8);
        int numBones = data[0x0D];
        int numQKeys = r.U16(0x0E);
        int numTKeys = r.U16(0x10);
        int numCustomKeys = r.U16(0x12);

        if ((flags & SkaFile.FlagUseCompressTable) != 0)
            return ParseThawCompressed(data, r, flags, duration, numBones, numCustomKeys, compressTable);
        if ((flags & SkaFile.FlagPlatform) != 0)
            return ParseThawHiRes(data, r, flags, duration, numBones, numQKeys, numTKeys, numCustomKeys);

        throw new InvalidDataException(
            $"THAW SKA: unrecognized flags 0x{flags:X8} (neither USECOMPRESSTABLE nor PLATFORM)");
    }

    private static SkaAnimation ParseThawCompressed(
        ReadOnlySpan<byte> data, EndianSpanReader r, uint flags, float duration,
        int numBones, int numCustomKeys, SkaCompressTable? compressTable)
    {
        var declaredQBytes = r.U32(0x28);
        var declaredTBytes = r.U32(0x2C);

        var off = 0x30;
        var sizeTableEnd = (long)off + 4L * numBones;
        if (sizeTableEnd > data.Length)
            throw new InvalidDataException(
                $"THAW SKA compressed: {numBones}-bone Q/T size tables overrun file");

        var qSizes = new int[numBones];
        for (var i = 0; i < numBones; i++, off += 2)
            qSizes[i] = r.U16(off);
        var tSizes = new int[numBones];
        for (var i = 0; i < numBones; i++, off += 2)
            tSizes[i] = r.U16(off);

        if ((flags & SkaFile.FlagPartialAnim) != 0)
        {
            // u32 original bone count + one mask u32 per 32 original bones,
            // between the size tables and the key blobs.
            if (off > data.Length - 4)
                throw new InvalidDataException("THAW SKA compressed: partial-animation header overruns file");

            var origBones = r.U32(off);
            var partialEnd = (long)off + 4 + 4 * (((long)origBones + 31) / 32);
            if (partialEnd > data.Length)
                throw new InvalidDataException("THAW SKA compressed: partial-animation mask overruns file");
            off = (int)partialEnd;
        }

        var qSizeTotal = qSizes.Sum(static size => (long)size);
        var tSizeTotal = tSizes.Sum(static size => (long)size);
        if (qSizeTotal != declaredQBytes)
            throw new InvalidDataException(
                $"THAW SKA compressed: Q size table totals {qSizeTotal} bytes, header declares {declaredQBytes}");
        if (tSizeTotal != declaredTBytes)
            throw new InvalidDataException(
                $"THAW SKA compressed: T size table totals {tSizeTotal} bytes, header declares {declaredTBytes}");

        var streamsEnd = (long)off + declaredQBytes + declaredTBytes;
        if (streamsEnd > data.Length)
            throw new InvalidDataException(
                $"THAW SKA compressed: Q/T blobs end at 0x{streamsEnd:X}, beyond file length 0x{data.Length:X}");

        var qBytes = checked((int)declaredQBytes);
        var tBytes = checked((int)declaredTBytes);

        var compact = (flags & FlagThawCompactKeys) != 0;
        var hiResTs = (flags & FlagThawHiResTimestamps) != 0;

        var tracks = new SkaBoneTrack[numBones];
        var qOff = off;
        var qEnd = off + qBytes;
        var tOff = qEnd;
        var tEnd = (int)streamsEnd;
        for (var bone = 0; bone < numBones; bone++)
        {
            var rotKeys = DecodeThawQKeys(data, ref qOff, qOff + qSizes[bone], compact, hiResTs, compressTable);
            var transKeys =
                SkaCompressedKeyDecoders.DecodeCompressedTKeys(data, ref tOff, tOff + tSizes[bone], compressTable);
            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                RotationKeys = rotKeys,
                TranslationKeys = transKeys
            };
        }

        if (qOff != qEnd)
            throw new InvalidDataException(
                $"THAW SKA: Q blobs consumed {qOff - off} of {qBytes} bytes");
        if (tOff != tEnd)
            throw new InvalidDataException(
                $"THAW SKA: T blobs consumed {tOff - off - qBytes} of {tBytes} bytes");

        var customKeys = ParseThawCustomKeys(data, r, tOff, numCustomKeys);

        return new SkaAnimation
        {
            Version = ThawVersion,
            Flags = flags,
            Duration = duration,
            BoneTracks = tracks,
            CustomKeys = customKeys
        };
    }

    private static SkaRotationKey[] DecodeThawQKeys(
        ReadOnlySpan<byte> data, ref int off, int end,
        bool compact, bool hiResTs, SkaCompressTable? table)
    {
        if (off < 0 || end < off || end > data.Length)
            throw new InvalidDataException(
                $"THAW SKA: Q track range 0x{off:X}..0x{end:X} is outside the file");

        var keys = new List<SkaRotationKey>();

        while (off < end)
        {
            int timestamp;
            if (hiResTs)
            {
                EnsureThawQTrackBytes(off, end, 2, "high-resolution timestamp");
                timestamp = (data[off] | (data[off + 1] << 8)) & 0x7FFF;
                off += 2;
            }
            else
            {
                timestamp = 0; // assigned from the header below
            }

            EnsureThawQTrackBytes(off, end, 2, "key header");
            var header = (ushort)(data[off] | (data[off + 1] << 8));
            var signBit = (header & 0x8000) != 0;
            off += 2;

            int payloadSize;
            if ((header & 0x4000) == 0)
            {
                payloadSize = compact ? 3 : 6;
            }
            else if ((header & 0x3800) == 0)
            {
                payloadSize = 1;
            }
            else
            {
                payloadSize = ThawQComponentSize((header & 0x2000) != 0, compact)
                              + ThawQComponentSize((header & 0x1000) != 0, compact)
                              + ThawQComponentSize((header & 0x0800) != 0, compact);
            }

            EnsureThawQTrackBytes(off, end, payloadSize, "key payload");

            float qx, qy, qz;
            if ((header & 0x4000) != 0)
            {
                if (!hiResTs)
                    timestamp = header & 0x07FF;

                if ((header & 0x3800) == 0)
                {
                    // Whole-key lookup into the Q table.
                    var index = data[off];
                    off += 1;
                    if (table != null)
                    {
                        qx = table.Q48[index].X / 16384f;
                        qy = table.Q48[index].Y / 16384f;
                        qz = table.Q48[index].Z / 16384f;
                    }
                    else
                    {
                        qx = qy = qz = 0;
                    }
                }
                else
                {
                    qx = ReadThawQComponent(data, ref off, (header & 0x2000) != 0, compact, table);
                    qy = ReadThawQComponent(data, ref off, (header & 0x1000) != 0, compact, table);
                    qz = ReadThawQComponent(data, ref off, (header & 0x0800) != 0, compact, table);
                }
            }
            else
            {
                // Direct key: 14 effective timestamp bits (bit14 is clear).
                if (!hiResTs)
                    timestamp = header & 0x7FFF;

                if (compact)
                {
                    qx = (short)(data[off] << 8) / 16384f;
                    qy = (short)(data[off + 1] << 8) / 16384f;
                    qz = (short)(data[off + 2] << 8) / 16384f;
                    off += 3;
                }
                else
                {
                    qx = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                    qy = (short)(data[off + 2] | (data[off + 3] << 8)) / 16384f;
                    qz = (short)(data[off + 4] | (data[off + 5] << 8)) / 16384f;
                    off += 6;
                }
            }

            keys.Add(new SkaRotationKey(timestamp / 60f, SkaFile.ReconstructQuat(qx, qy, qz, signBit)));
        }

        if (off != end)
            throw new InvalidDataException($"THAW SKA: Q track consumed past its size table entry ({off} != {end})");

        return keys.ToArray();
    }

    private static int ThawQComponentSize(bool narrow, bool compact) => narrow || compact ? 1 : 2;

    private static void EnsureThawQTrackBytes(int offset, int end, int count, string context)
    {
        if (offset < 0 || count < 0 || (long)offset + count > end)
        {
            throw new InvalidDataException(
                $"THAW SKA: Q track {context} overruns its size-table entry at 0x{offset:X} " +
                $"(need {count} bytes, end 0x{end:X})");
        }
    }

    /// <summary>
    ///     One variable-width Q component. THAW always sets flags bit16, so a
    ///     byte-width component is an index into the scalar column of the Q
    ///     compress table; a full-width component is a raw s16 — except in
    ///     compact mode (bit15), where it is a single byte shifted into the
    ///     s16's high bits.
    /// </summary>
    private static float ReadThawQComponent(
        ReadOnlySpan<byte> data, ref int off, bool narrow, bool compact, SkaCompressTable? table)
    {
        short value;
        if (narrow)
        {
            value = table?.Q48[data[off]].Scalar ?? 0;
            off += 1;
        }
        else if (compact)
        {
            value = (short)(data[off] << 8);
            off += 1;
        }
        else
        {
            value = (short)(data[off] | (data[off + 1] << 8));
            off += 2;
        }

        return value / 16384f;
    }

    private static SkaAnimation ParseThawHiRes(
        ReadOnlySpan<byte> data, EndianSpanReader r, uint flags, float duration,
        int numBones, int numQKeys, int numTKeys, int numCustomKeys)
    {
        var off = 0x28;

        uint[]? boneNames = null;
        if ((flags & SkaFile.FlagObjectAnimData) != 0)
        {
            var boneNamesEnd = (long)off + 4L * numBones;
            if (boneNamesEnd > data.Length)
                throw new InvalidDataException("THAW SKA hi-res: bone-name table overruns file");

            boneNames = new uint[numBones];
            for (var i = 0; i < numBones; i++, off += 4)
                boneNames[i] = r.U32(off);
        }

        if ((flags & SkaFile.FlagPartialAnim) != 0)
        {
            if (off > data.Length - 4)
                throw new InvalidDataException("THAW SKA hi-res: partial-animation header overruns file");

            var origBones = r.U32(off);
            var partialEnd = (long)off + 4 + 4 * (((long)origBones + 31) / 32);
            if (partialEnd > data.Length)
                throw new InvalidDataException("THAW SKA hi-res: partial-animation mask overruns file");
            off = (int)partialEnd;
        }

        // Per-bone key counts: u8 pairs, or u16 pairs when a track exceeds 255
        // keys (bit22 HIRESFRAMEPOINTERS, same as THUG).
        var qCounts = new int[numBones];
        var tCounts = new int[numBones];
        if ((flags & SkaFile.FlagHiResFramePointers) != 0)
        {
            var countTableEnd = (long)off + 4L * numBones;
            if (countTableEnd > data.Length)
                throw new InvalidDataException("THAW SKA hi-res: Q/T count table overruns file");

            for (var i = 0; i < numBones; i++)
            {
                qCounts[i] = r.U16(off + 4 * i);
                tCounts[i] = r.U16(off + 4 * i + 2);
            }

            off += 4 * numBones;
        }
        else
        {
            var countTableEnd = (long)off + 2L * numBones;
            if (countTableEnd > data.Length)
                throw new InvalidDataException("THAW SKA hi-res: Q/T count table overruns file");

            for (var i = 0; i < numBones; i++)
            {
                qCounts[i] = data[off + 2 * i];
                tCounts[i] = data[off + 2 * i + 1];
            }

            off += 2 * numBones;
        }

        var qCountTotal = qCounts.Sum();
        var tCountTotal = tCounts.Sum();
        if (qCountTotal != numQKeys)
            throw new InvalidDataException(
                $"THAW SKA hi-res: per-bone Q counts total {qCountTotal}, header declares {numQKeys}");
        if (tCountTotal != numTKeys)
            throw new InvalidDataException(
                $"THAW SKA hi-res: per-bone T counts total {tCountTotal}, header declares {numTKeys}");

        if ((off & 3) != 0)
            off += 4 - (off & 3);

        // 16-byte hi-res keys: u16 (timestamp:15 | signBit:15th), pad, 3 × f32.
        var qStart = off;
        var tStartLong = (long)off + 16L * numQKeys;
        var endOfKeysLong = tStartLong + 16L * numTKeys;
        if (endOfKeysLong > data.Length)
            throw new InvalidDataException(
                $"THAW SKA hi-res: {numQKeys}Q+{numTKeys}T keys overrun file ({endOfKeysLong} > {data.Length})");
        var tStart = (int)tStartLong;
        var endOfKeys = (int)endOfKeysLong;

        var tracks = new SkaBoneTrack[numBones];
        var qOff = qStart;
        var tOff = tStart;
        for (var bone = 0; bone < numBones; bone++)
        {
            var rotKeys = new SkaRotationKey[qCounts[bone]];
            for (var k = 0; k < rotKeys.Length; k++, qOff += 16)
            {
                var ts = r.U16(qOff);
                rotKeys[k] = new SkaRotationKey(
                    (ts & 0x7FFF) / 60f,
                    SkaFile.ReconstructQuat(r.F32(qOff + 4), r.F32(qOff + 8), r.F32(qOff + 12), (ts & 0x8000) != 0));
            }

            var transKeys = new SkaTranslationKey[tCounts[bone]];
            for (var k = 0; k < transKeys.Length; k++, tOff += 16)
            {
                transKeys[k] = new SkaTranslationKey(
                    (r.U16(tOff) & 0x7FFF) / 60f,
                    new Vector3(r.F32(tOff + 4), r.F32(tOff + 8), r.F32(tOff + 12)));
            }

            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                RotationKeys = rotKeys,
                TranslationKeys = transKeys,
                BoneNameChecksum = boneNames?[bone]
            };
        }

        if (qOff != tStart || tOff != endOfKeys)
            throw new InvalidDataException(
                $"THAW SKA hi-res: per-bone Q/T counts did not consume their declared streams");

        var customKeys = ParseThawCustomKeys(data, r, endOfKeys, numCustomKeys);

        return new SkaAnimation
        {
            Version = ThawVersion,
            Flags = flags,
            Duration = duration,
            BoneTracks = tracks,
            CustomKeys = customKeys
        };
    }

    /// <summary>
    ///     Custom keys share a 12-byte header: raw timestamp, type and total record
    ///     size. The stream starts on the next four-byte boundary after Q/T.
    ///     Only the two payloads present in THAW are interpreted here; every
    ///     payload remains available as raw bytes so later engine types are not
    ///     discarded.
    /// </summary>
    private static SkaCustomKey[] ParseThawCustomKeys(
        ReadOnlySpan<byte> data, EndianSpanReader r, int offset, int count)
    {
        offset = (offset + 3) & ~3;
        if (count == 0)
        {
            if (offset != data.Length)
                throw new InvalidDataException(
                    $"THAW SKA declares no custom keys, but {data.Length - offset} trailing bytes remain");
            return [];
        }

        var keys = new SkaCustomKey[count];
        for (var i = 0; i < keys.Length; i++)
        {
            if (offset < 0 || offset > data.Length - 12)
                throw new InvalidDataException(
                    $"THAW SKA custom key {i}: 12-byte header overruns file at 0x{offset:X}");

            var timestamp = r.U32(offset);
            var type = r.U32(offset + 4);
            var size = r.U32(offset + 8);
            if (size < 12)
                throw new InvalidDataException(
                    $"THAW SKA custom key {i}: record size {size} is smaller than its 12-byte header");
            if ((size & 3) != 0)
                throw new InvalidDataException(
                    $"THAW SKA custom key {i}: record size {size} is not four-byte aligned");

            var end = (long)offset + size;
            if (end > data.Length)
                throw new InvalidDataException(
                    $"THAW SKA custom key {i}: record end 0x{end:X} exceeds file length 0x{data.Length:X}");

            if ((type == 1 || type == 4) && size != 16)
                throw new InvalidDataException(
                    $"THAW SKA custom key {i}: decoded type {type} must be a 16-byte record (size {size})");

            var payloadLength = checked((int)size - 12);
            var payload = data.Slice(offset + 12, payloadLength).ToArray();
            keys[i] = new SkaCustomKey
            {
                Timestamp = timestamp,
                Type = type,
                Size = size,
                Payload = payload,
                Fov = type == 1 ? r.F32(offset + 12) : null,
                ScriptQbKey = type == 4 ? r.U32(offset + 12) : null
            };

            offset = checked((int)end);
        }

        if (offset != data.Length)
            throw new InvalidDataException(
                $"THAW SKA custom keys end at 0x{offset:X}, but file length is 0x{data.Length:X}");

        return keys;
    }
}
