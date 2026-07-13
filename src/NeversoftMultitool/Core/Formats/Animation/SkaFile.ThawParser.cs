using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Animation;

internal static partial class SkaFile
{
    // THAW v0x28 container (THAW PS2/PC/GC + Project 8 + Proving Ground).
    // Little-endian on PS2/PC, big-endian on GC for the header, size tables,
    // partial-anim prelude, bone-name arrays and hi-res float keys; the
    // COMPRESSED key blobs and the standardkey compress tables ship as raw
    // little-endian byte streams on every platform (the GC port swaps at
    // runtime, same as THAW QB script bodies).
    //
    // Layout (validated on 7,057 PS2<->GC pairs; key grammar verified against
    // the THAW PS2 ELF readers, tools/ghidra/thaw-ps2/output/phase_ska_key_readers.c):
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
    //        [custom keys — event data, not consumed here]
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
    private const uint FlagPartialAnim = 1u << 19;
    private const uint FlagObjectAnimData = 1u << 24;

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
        return (flags & (FlagUseCompressTable | FlagPlatform)) != 0;
    }

    private static SkaAnimation ParseThaw(ReadOnlySpan<byte> data, bool bigEndian, SkaCompressTable? compressTable)
    {
        var r = new EndianSpanReader(data, bigEndian);
        var flags = r.U32(4);
        var duration = r.F32(8);
        int numBones = data[0x0D];
        int numQKeys = r.U16(0x0E);
        int numTKeys = r.U16(0x10);

        if ((flags & FlagUseCompressTable) != 0)
            return ParseThawCompressed(data, r, flags, duration, numBones, compressTable);
        if ((flags & FlagPlatform) != 0)
            return ParseThawHiRes(data, r, flags, duration, numBones, numQKeys, numTKeys);

        throw new InvalidDataException(
            $"THAW SKA: unrecognized flags 0x{flags:X8} (neither USECOMPRESSTABLE nor PLATFORM)");
    }

    private static SkaAnimation ParseThawCompressed(
        ReadOnlySpan<byte> data, EndianSpanReader r, uint flags, float duration,
        int numBones, SkaCompressTable? compressTable)
    {
        var qBytes = (int)r.U32(0x28);
        var tBytes = (int)r.U32(0x2C);

        var off = 0x30;
        var qSizes = new int[numBones];
        for (var i = 0; i < numBones; i++, off += 2)
            qSizes[i] = r.U16(off);
        var tSizes = new int[numBones];
        for (var i = 0; i < numBones; i++, off += 2)
            tSizes[i] = r.U16(off);

        if ((flags & FlagPartialAnim) != 0)
        {
            // u32 original bone count + one mask u32 per 32 original bones,
            // between the size tables and the key blobs.
            var origBones = (int)r.U32(off);
            off += 4 + 4 * ((origBones - 1) / 32 + 1);
        }

        var compact = (flags & FlagThawCompactKeys) != 0;
        var hiResTs = (flags & FlagThawHiResTimestamps) != 0;

        var tracks = new SkaBoneTrack[numBones];
        var qOff = off;
        var tOff = off + qBytes;
        for (var bone = 0; bone < numBones; bone++)
        {
            var rotKeys = DecodeThawQKeys(data, ref qOff, qOff + qSizes[bone], compact, hiResTs, compressTable);
            var transKeys = DecodeCompressedTKeys(data, ref tOff, tOff + tSizes[bone], compressTable);
            tracks[bone] = new SkaBoneTrack
            {
                BoneIndex = bone,
                RotationKeys = rotKeys,
                TranslationKeys = transKeys
            };
        }

        if (tOff != off + qBytes + tBytes)
            throw new InvalidDataException(
                $"THAW SKA: T blobs consumed {tOff - off - qBytes} of {tBytes} bytes");

        return new SkaAnimation
        {
            Version = ThawVersion,
            Flags = flags,
            Duration = duration,
            BoneTracks = tracks
        };
    }

    private static SkaRotationKey[] DecodeThawQKeys(
        ReadOnlySpan<byte> data, ref int off, int end,
        bool compact, bool hiResTs, SkaCompressTable? table)
    {
        var keys = new List<SkaRotationKey>();

        while (off < end)
        {
            int timestamp;
            if (hiResTs)
            {
                timestamp = (data[off] | (data[off + 1] << 8)) & 0x7FFF;
                off += 2;
            }
            else
            {
                timestamp = 0; // assigned from the header below
            }

            var header = (ushort)(data[off] | (data[off + 1] << 8));
            var signBit = (header & 0x8000) != 0;
            off += 2;

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

            keys.Add(new SkaRotationKey(timestamp / 60f, ReconstructQuat(qx, qy, qz, signBit)));
        }

        if (off != end)
            throw new InvalidDataException($"THAW SKA: Q track consumed past its size table entry ({off} != {end})");

        return keys.ToArray();
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
            value = table?.Q48[data[off]].Scalar ?? (short)0;
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
        int numBones, int numQKeys, int numTKeys)
    {
        var off = 0x28;

        uint[]? boneNames = null;
        if ((flags & FlagObjectAnimData) != 0)
        {
            boneNames = new uint[numBones];
            for (var i = 0; i < numBones; i++, off += 4)
                boneNames[i] = r.U32(off);
        }

        if ((flags & FlagPartialAnim) != 0)
        {
            var origBones = (int)r.U32(off);
            off += 4 + 4 * ((origBones - 1) / 32 + 1);
        }

        // Per-bone key counts: u8 pairs, or u16 pairs when a track exceeds 255
        // keys (bit22 HIRESFRAMEPOINTERS, same as THUG).
        var qCounts = new int[numBones];
        var tCounts = new int[numBones];
        if ((flags & FlagHiResFramePointers) != 0)
        {
            for (var i = 0; i < numBones; i++)
            {
                qCounts[i] = r.U16(off + 4 * i);
                tCounts[i] = r.U16(off + 4 * i + 2);
            }

            off += 4 * numBones;
        }
        else
        {
            for (var i = 0; i < numBones; i++)
            {
                qCounts[i] = data[off + 2 * i];
                tCounts[i] = data[off + 2 * i + 1];
            }

            off += 2 * numBones;
        }

        if ((off & 3) != 0)
            off += 4 - (off & 3);

        // 16-byte hi-res keys: u16 (timestamp:15 | signBit:15th), pad, 3 × f32.
        var qStart = off;
        var tStart = off + 16 * numQKeys;
        var endOfKeys = tStart + 16 * numTKeys;
        if (endOfKeys > data.Length)
            throw new InvalidDataException(
                $"THAW SKA hi-res: {numQKeys}Q+{numTKeys}T keys overrun file ({endOfKeys} > {data.Length})");

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
                    ReconstructQuat(r.F32(qOff + 4), r.F32(qOff + 8), r.F32(qOff + 12), (ts & 0x8000) != 0));
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

        // Custom anim keys (event data) may follow — intentionally not consumed.
        return new SkaAnimation
        {
            Version = ThawVersion,
            Flags = flags,
            Duration = duration,
            BoneTracks = tracks
        };
    }
}
