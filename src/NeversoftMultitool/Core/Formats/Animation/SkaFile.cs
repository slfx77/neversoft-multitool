using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Parser for Neversoft SKA animation files (THPS4/THUG/THUG2).
///     Format reference: THUG source Gfx/BonedAnim.cpp + BonedAnimTypes.h.
///
///     File layout (USECOMPRESSTABLE path — flags bit 23):
///     <code>
///     [File header]       12 bytes: version(u32) + flags(u32) + duration(float)
///     [Platform header]   16 bytes: numBones(u32) + numQKeys(u32) + numTKeys(u32) + numCustomKeys(u32)
///     [Alloc sizes]        8 bytes: qAllocSize(u32) + tAllocSize(u32)
///     [Per-bone Q sizes]  numBones × u16
///     [Per-bone T sizes]  numBones × u16
///     [4-byte alignment pad]
///     [Q keyframe data]   qAllocSize bytes (variable-length compressed keys)
///     [T keyframe data]   tAllocSize bytes (variable-length compressed keys)
///     </code>
///
///     File layout (PLATFORM path — flags bit 28):
///     <code>
///     [File header]       12 bytes
///     [Platform header]   16 bytes
///     [Per-bone frames]   numBones × 2 bytes (standard) or × 4 bytes (hi-res)
///     [4-byte alignment pad]
///     [Q keyframe data]   numQKeys × 8 bytes (standard) or × 14 bytes (hi-res)
///     [T keyframe data]   numTKeys × 8 bytes (standard) or × 14 bytes (hi-res)
///     </code>
/// </summary>
internal static class SkaFile
{
    private const uint FlagPlatform = 1u << 28;
    private const uint FlagCompressedTime = 1u << 26;
    private const uint FlagPreRotatedRoot = 1u << 25;
    private const uint FlagUseCompressTable = 1u << 23;
    private const uint FlagHiResFramePointers = 1u << 22;

    /// <summary>Quick check: does this look like a valid SKA file?</summary>
    internal static bool IsSkaFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28) return false;
        var flags = BitConverter.ToUInt32(data[4..]);
        return (flags & FlagPlatform) != 0 || (flags & FlagUseCompressTable) != 0;
    }

    internal static SkaAnimation Parse(byte[] data, SkaCompressTable? compressTable = null)
    {
        return Parse((ReadOnlySpan<byte>)data, compressTable);
    }

    internal static SkaAnimation Parse(ReadOnlySpan<byte> data, SkaCompressTable? compressTable = null)
    {
        // File header (12 bytes)
        var version = BitConverter.ToUInt32(data);
        var flags = BitConverter.ToUInt32(data[4..]);
        var duration = BitConverter.ToSingle(data[8..]);

        if ((flags & FlagUseCompressTable) != 0)
            return ParseCompressed(data, version, flags, duration, compressTable);
        if ((flags & FlagPlatform) != 0)
            return ParsePlatform(data, version, flags, duration);

        throw new InvalidDataException($"SKA: unrecognized flags 0x{flags:X8} (neither PLATFORM nor USECOMPRESSTABLE)");
    }

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

    private static SkaRotationKey[] DecodeCompressedQKeys(
        ReadOnlySpan<byte> data, ref int off, int end, float duration,
        bool compressedTime, SkaCompressTable? table)
    {
        var keys = new List<SkaRotationKey>();

        while (off < end)
        {
            var header = (ushort)(data[off] | (data[off + 1] << 8));
            var signBit = (header & 0x8000) != 0;
            off += 2;

            float qx, qy, qz;
            int timestamp;

            if ((header & 0x4000) != 0)
            {
                if ((header & 0x3800) == 0)
                {
                    // Table lookup: 1 byte index
                    timestamp = header & 0x07FF; // 11-bit timestamp
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
                        // No table — use identity as fallback
                        qx = qy = qz = 0;
                    }
                }
                else
                {
                    // Per-component variable encoding
                    timestamp = header & 0x07FF; // 11-bit timestamp

                    if ((header & 0x2000) != 0)
                    {
                        qx = (sbyte)data[off] / 16384f;
                        off += 1;
                    }
                    else
                    {
                        qx = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                        off += 2;
                    }

                    if ((header & 0x1000) != 0)
                    {
                        qy = (sbyte)data[off] / 16384f;
                        off += 1;
                    }
                    else
                    {
                        qy = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                        off += 2;
                    }

                    if ((header & 0x0800) != 0)
                    {
                        qz = (sbyte)data[off] / 16384f;
                        off += 1;
                    }
                    else
                    {
                        qz = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                        off += 2;
                    }
                }
            }
            else
            {
                // Direct: 3 × int16
                timestamp = header & 0x3FFF; // 14-bit timestamp
                qx = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                qy = (short)(data[off + 2] | (data[off + 3] << 8)) / 16384f;
                qz = (short)(data[off + 4] | (data[off + 5] << 8)) / 16384f;
                off += 6;
            }

            var time = timestamp / 60f;
            keys.Add(new SkaRotationKey(time, ReconstructQuat(qx, qy, qz, signBit)));
        }

        return keys.ToArray();
    }

    private static SkaTranslationKey[] DecodeCompressedTKeys(
        ReadOnlySpan<byte> data, ref int off, int end, float duration,
        bool compressedTime, SkaCompressTable? table)
    {
        var keys = new List<SkaTranslationKey>();

        while (off < end)
        {
            var flagByte = data[off];
            off += 1;

            var useLookup = (flagByte & 0x80) != 0;
            int timestamp;

            if ((flagByte & 0x40) != 0)
            {
                // Short timestamp: 6 bits inline
                timestamp = flagByte & 0x3F;
            }
            else
            {
                // Full timestamp: next u16
                timestamp = data[off] | (data[off + 1] << 8);
                off += 2;
            }

            float tx, ty, tz;

            if (useLookup)
            {
                var index = data[off];
                off += 1;

                if (table != null)
                {
                    tx = table.T48[index].X / 32f;
                    ty = table.T48[index].Y / 32f;
                    tz = table.T48[index].Z / 32f;
                }
                else
                {
                    tx = ty = tz = 0;
                }
            }
            else
            {
                // Direct: 3 × int16
                tx = (short)(data[off] | (data[off + 1] << 8)) / 32f;
                ty = (short)(data[off + 2] | (data[off + 3] << 8)) / 32f;
                tz = (short)(data[off + 4] | (data[off + 5] << 8)) / 32f;
                off += 6;
            }

            var time = timestamp / 60f;
            keys.Add(new SkaTranslationKey(time, new Vector3(tx, ty, tz)));
        }

        return keys.ToArray();
    }

    /// <summary>
    ///     Reconstruct unit quaternion from SKA-file components and apply axis
    ///     remap from the nxtools FromSKAQuat helper:
    ///     <c>(x, y, z) → (-z, -x, -y)</c>.
    ///     SKA stores rotations in a (yaw-x, roll-y, pitch-z) axis convention;
    ///     glTF (and standard math) use (pitch-x, yaw-y, roll-z) with flipped signs.
    /// </summary>
    private static Quaternion ReconstructQuat(float x, float y, float z, bool signBit)
    {
        if (x == 0 && y == 0 && z == 0)
            return Quaternion.Identity;

        var sum = 1f - x * x - y * y - z * z;
        var w = sum > 0 ? MathF.Sqrt(sum) : 0f;
        if (signBit) w = -w;

        // Axis remap: file(x,y,z) → out(-z, -x, -y)
        return new Quaternion(-z, -x, -y, w);
    }
}

/// <summary>
///     Q48/T48 compression lookup tables (256 entries each).
///     Loaded from external files, shared across all animations.
/// </summary>
internal sealed class SkaCompressTable
{
    public required SkaCompressEntry[] Q48 { get; init; }
    public required SkaCompressEntry[] T48 { get; init; }

    /// <summary>
    ///     Load Q48/T48 tables from raw binary files.
    ///     Each file is 2048 bytes (256 entries × 8 bytes: x48(s16) + y48(s16) + z48(s16) + n8(s16)).
    /// </summary>
    internal static SkaCompressTable? TryLoad(string q48Path, string t48Path)
    {
        if (!File.Exists(q48Path) || !File.Exists(t48Path))
            return null;

        var q48Data = File.ReadAllBytes(q48Path);
        var t48Data = File.ReadAllBytes(t48Path);

        if (q48Data.Length < 2048 || t48Data.Length < 2048)
            return null;

        return new SkaCompressTable
        {
            Q48 = ParseEntries(q48Data),
            T48 = ParseEntries(t48Data)
        };
    }

    private static SkaCompressEntry[] ParseEntries(byte[] data)
    {
        var entries = new SkaCompressEntry[256];
        for (var i = 0; i < 256; i++)
        {
            var off = i * 8;
            entries[i] = new SkaCompressEntry(
                BitConverter.ToInt16(data, off),
                BitConverter.ToInt16(data, off + 2),
                BitConverter.ToInt16(data, off + 4));
        }
        return entries;
    }
}

internal readonly struct SkaCompressEntry(short x, short y, short z)
{
    public short X { get; } = x;
    public short Y { get; } = y;
    public short Z { get; } = z;
}
