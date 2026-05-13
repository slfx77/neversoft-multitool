using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Parser for Neversoft SKA animation files (THPS4/THUG/THUG2).
///     Format reference: THUG source Gfx/BonedAnim.cpp + BonedAnimTypes.h.
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

    // THPS3 uses RenderWare rpHAnim instead of Neversoft's BonedAnim engine.
    // Discriminator: flags has bit 31 set, PLATFORM/USECOMPRESSTABLE clear.
    private const uint FlagThps3RpHAnim = 1u << 31;

    /// <summary>Quick check: does this look like a valid SKA file?</summary>
    internal static bool IsSkaFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28) return false;
        var flags = BitConverter.ToUInt32(data[4..]);
        return (flags & FlagPlatform) != 0
               || (flags & FlagUseCompressTable) != 0
               || (flags & FlagThps3RpHAnim) != 0;
    }

    /// <summary>
    ///     Header-only probe for animation discovery. Returns duration and bone
    ///     count without decoding keyframes — does not require a compress table.
    ///     <see cref="SkaProbeResult.BoneCount" /> is null when unknown (THPS3,
    ///     where the count is implicit and only the full parser can derive it).
    /// </summary>
    internal static SkaProbeResult? TryProbe(ReadOnlySpan<byte> data)
    {
        if (!IsSkaFile(data)) return null;
        var flags = BitConverter.ToUInt32(data[4..]);
        var duration = BitConverter.ToSingle(data[8..]);

        if (((flags & FlagPlatform) != 0 || (flags & FlagUseCompressTable) != 0)
            && data.Length >= 16)
        {
            var numBones = (int)BitConverter.ToUInt32(data[12..]);
            return new SkaProbeResult(duration, numBones);
        }

        // THPS3 RpHAnim has no explicit bone count in the header; signal "unknown".
        return new SkaProbeResult(duration, null);
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
        if ((flags & FlagThps3RpHAnim) != 0)
            return ParseThps3(data, version, flags, duration);

        throw new InvalidDataException(
            $"SKA: unrecognized flags 0x{flags:X8} (neither PLATFORM nor USECOMPRESSTABLE nor THPS3)");
    }

    /// <summary>
    ///     Parse THPS3 PS2 SKA format (RenderWare rpHAnim variant).
    ///     File layout (verified on Bird_A_Flap 524 B + Crowd_A_CrowdClap 6844 B):
    ///     <code>
    ///     [File header]       28 bytes: version(u32) + flags(u32) + duration(f32)
    ///                                   + numQKeys(u32) + numTKeys(u32) + unk[2](u32)
    ///     [Pre-Q metadata]    12 bytes: reserved (possibly interpolation-scheme ID)
    ///     [Q keyframes]       (numQKeys − 1) × 24 B: prev(i32) + quat(4×f32) + time(f32)
    ///     [T keyframes]       numTKeys × 20 B: trans(3×f32) + time(f32) + prev(i32)
    ///     [Trailing pad]      4 bytes
    ///     </code>
    ///     T uses <c>prev-at-end</c>. numQKeys is always 1 greater than the
    ///     actual stored record count (RW allocates an extra slot at serialise
    ///     time). T <c>prev</c> is a byte offset back into the array, chaining
    ///     same-bone keys. A bone's first T key has <c>prev</c> set to a
    ///     per-file sentinel value (an uninitialised pointer from RW's writer).
    ///     Q <c>prev</c> does not identify runtime bone tracks; the game loads
    ///     Q records into non-root bone tracks using serialized time order.
    ///     Record strides and field offsets were confirmed against the THPS3
    ///     PS2 in-memory interpolator (FUN_00230f68 / FUN_00231048 at
    ///     SLUS_200.13 +0x230F68): 0x18 stride for Q, 0x14 stride for T,
    ///     Hamilton product composing quat.w via <c>pfVar[3]</c>.
    /// </summary>
    private static SkaAnimation ParseThps3(
        ReadOnlySpan<byte> data, uint version, uint flags, float duration)
    {
        const int HeaderSize = 28;
        const int PreQMetadata = 12;
        const int QTPadding = 4;
        const int QRecordSize = 24;
        const int TRecordSize = 20;

        var numQKeys = (int)BitConverter.ToUInt32(data[12..]);
        var numTKeys = (int)BitConverter.ToUInt32(data[16..]);

        // Actual Q records stored = numQKeys - 1 (header over-counts by 1).
        // A 4-byte field sits between Q and T — purpose unknown, possibly a
        // trailing end-of-Q marker baked into the allocation.
        var qActual = Math.Max(0, numQKeys - 1);
        var qStart = HeaderSize + PreQMetadata;
        var qEnd = qStart + qActual * QRecordSize;
        var tStart = qEnd + QTPadding;
        var tEnd = tStart + numTKeys * TRecordSize;

        if (tEnd > data.Length)
        {
            // Don't crash on short files — cap numTKeys to what actually fits.
            numTKeys = Math.Max(0, (data.Length - tStart) / TRecordSize);
            tEnd = tStart + numTKeys * TRecordSize;
        }

        var (qKeys, qSentinels) = ReadThps3Records(data, qStart, qActual, QRecordSize, ThpsRecordKind.Q);
        var (tKeys, tSentinels) = ReadThps3Records(data, tStart, numTKeys, TRecordSize, ThpsRecordKind.T);

        var tTracksByBone = AssignBonesByPrevChain(tKeys, tSentinels, TRecordSize);
        var qRuntimeTrackCount = Math.Max(0, (tTracksByBone.Length > 0 ? tTracksByBone.Length : qSentinels.Count) - 1);
        var qTracksByNonRootBone = AssignThps3RuntimeQTracks(qKeys, qRuntimeTrackCount, duration);

        // The runtime Q blob contains tracks for bones 1..N only. Bone 0/root
        // has implicit identity rotation; translations still include root.
        var numBones = Math.Max(tTracksByBone.Length, qTracksByNonRootBone.Length + 1);

        // Build per-bone tracks. Bones without a T track get an empty array.
        var tracks = new SkaBoneTrack[numBones];
        for (var bone = 0; bone < numBones; bone++)
        {
            var rot = bone > 0 && bone - 1 < qTracksByNonRootBone.Length
                ? qTracksByNonRootBone[bone - 1]
                : Array.Empty<ThpsRawKey>();
            var dedupedRot = DedupByTimeKeepFirst(rot);
            var rotKeys = new SkaRotationKey[dedupedRot.Count];
            for (var k = 0; k < dedupedRot.Count; k++)
            {
                var src = dedupedRot[k];
                var q = Quaternion.Normalize(new Quaternion(src.X, src.Y, src.Z, src.W));
                rotKeys[k] = new SkaRotationKey(src.Time, q);
            }

            Array.Sort(rotKeys, (a, b) => a.Time.CompareTo(b.Time));

            var trans = bone < tTracksByBone.Length ? tTracksByBone[bone] : Array.Empty<ThpsRawKey>();
            var dedupedTrans = DedupByTimeKeepFirst(trans);
            var transKeys = new SkaTranslationKey[dedupedTrans.Count];
            for (var k = 0; k < dedupedTrans.Count; k++)
            {
                var src = dedupedTrans[k];
                transKeys[k] = new SkaTranslationKey(src.Time, new Vector3(src.X, src.Y, src.Z));
            }

            Array.Sort(transKeys, (a, b) => a.Time.CompareTo(b.Time));

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

    // THPS3 SKA files authored in the Maya exporter can contain multiple
    // records at the same timestamp for a track. Runtime evidence favors the
    // first serialized value for a timestamp; dropping later duplicates also
    // prevents glTF interpolation from passing through stale intermediate keys.
    private static List<ThpsRawKey> DedupByTimeKeepFirst(ThpsRawKey[] keys)
    {
        if (keys.Length <= 1) return new List<ThpsRawKey>(keys);

        // Group by rounded timestamp; within each group pick the LOWEST RecIndex
        // (first-inserted = most "canonical" value in the authoring tool's history).
        // Tried HIGHEST first but bone rotations still looked spasmy; FIRST tends
        // to produce smoother keyframe transitions in practice.
        var byTime = new Dictionary<long, ThpsRawKey>();
        foreach (var k in keys)
        {
            var bucket = (long)MathF.Round(k.Time * 60f);
            if (!byTime.TryGetValue(bucket, out var existing) || k.RecIndex < existing.RecIndex)
                byTime[bucket] = k;
        }

        return byTime.Values.ToList();
    }

    private static ThpsRawKey[][] AssignThps3RuntimeQTracks(
        ThpsRawKey[] keys, int trackCount, float duration)
    {
        if (trackCount <= 0)
            return Array.Empty<ThpsRawKey[]>();

        var result = new List<ThpsRawKey>[trackCount];
        for (var track = 0; track < trackCount; track++)
            result[track] = new List<ThpsRawKey>();

        var initialCount = Math.Min(trackCount, keys.Length);
        for (var i = 0; i < initialCount; i++)
            result[i].Add(keys[i]);

        if (keys.Length <= trackCount)
            return result.Select(static track => track.ToArray()).ToArray();

        // The game omits the root/end marker from the loaded Q blob. For the
        // skater rig this is record 28: identity at animation duration.
        var start = IsImplicitRootQMarker(keys[trackCount], duration)
            ? trackCount + 1
            : trackCount;

        for (var i = start; i < keys.Length; i++)
        {
            var key = keys[i];
            var bestTrack = -1;
            var bestTime = 0f;

            for (var track = 0; track < result.Length; track++)
            {
                if (result[track].Count == 0)
                {
                    bestTrack = track;
                    bestTime = float.NegativeInfinity;
                    break;
                }

                var lastTime = result[track][^1].Time;
                if (lastTime > key.Time + 0.0001f)
                    continue;

                if (bestTrack < 0 || lastTime < bestTime - 0.0001f)
                {
                    bestTrack = track;
                    bestTime = lastTime;
                }
            }

            if (bestTrack < 0)
                bestTrack = FindTrackWithLowestLastTime(result);

            result[bestTrack].Add(key);
        }

        return result.Select(static track => track.ToArray()).ToArray();
    }

    private static bool IsImplicitRootQMarker(ThpsRawKey key, float duration)
    {
        const float QTolerance = 0.0001f;
        const float TimeTolerance = 1f / 30f;

        return MathF.Abs(key.X) <= QTolerance
               && MathF.Abs(key.Y) <= QTolerance
               && MathF.Abs(key.Z) <= QTolerance
               && MathF.Abs(MathF.Abs(key.W) - 1f) <= QTolerance
               && MathF.Abs(key.Time - duration) <= TimeTolerance;
    }

    private static int FindTrackWithLowestLastTime(List<ThpsRawKey>[] tracks)
    {
        var bestTrack = 0;
        var bestTime = tracks[0].Count > 0 ? tracks[0][^1].Time : float.NegativeInfinity;
        for (var track = 1; track < tracks.Length; track++)
        {
            var lastTime = tracks[track].Count > 0 ? tracks[track][^1].Time : float.NegativeInfinity;
            if (lastTime < bestTime)
            {
                bestTrack = track;
                bestTime = lastTime;
            }
        }

        return bestTrack;
    }

    private static (ThpsRawKey[] keys, List<int> sentinelIndices) ReadThps3Records(
        ReadOnlySpan<byte> data, int start, int count, int stride, ThpsRecordKind trackKind)
    {
        var keys = new ThpsRawKey[count];
        var sentinels = new List<int>();

        for (var i = 0; i < count; i++)
        {
            var off = start + i * stride;
            int prev;
            float x, y, z, w = 0f, time;

            if (trackKind == ThpsRecordKind.Q)
            {
                // Q: prev(4) + quat(16) + time(4)
                prev = BitConverter.ToInt32(data[off..]);
                x = BitConverter.ToSingle(data[(off + 4)..]);
                y = BitConverter.ToSingle(data[(off + 8)..]);
                z = BitConverter.ToSingle(data[(off + 12)..]);
                w = BitConverter.ToSingle(data[(off + 16)..]);
                time = BitConverter.ToSingle(data[(off + 20)..]);
            }
            else
            {
                // T: trans(12) + time(4) + prev(4)
                x = BitConverter.ToSingle(data[off..]);
                y = BitConverter.ToSingle(data[(off + 4)..]);
                z = BitConverter.ToSingle(data[(off + 8)..]);
                time = BitConverter.ToSingle(data[(off + 12)..]);
                prev = BitConverter.ToInt32(data[(off + 16)..]);
            }

            keys[i] = new ThpsRawKey(x, y, z, w, time, prev, i);

            // Sentinel = prev doesn't land on an earlier record in this array.
            // Valid prev: non-negative, a multiple of stride, and < (i * stride).
            var isSentinel = prev < 0 || prev >= i * stride || prev % stride != 0;
            if (isSentinel) sentinels.Add(i);
        }

        return (keys, sentinels);
    }

    private static ThpsRawKey[][] AssignBonesByPrevChain(
        ThpsRawKey[] keys, List<int> sentinelIndices, int stride)
    {
        var numBones = sentinelIndices.Count;
        if (numBones == 0) return Array.Empty<ThpsRawKey[]>();

        var boneOf = new int[keys.Length];
        var sentinelRank = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            if (sentinelRank < sentinelIndices.Count && sentinelIndices[sentinelRank] == i)
            {
                boneOf[i] = sentinelRank++;
            }
            else
            {
                var prevIdx = keys[i].Prev / stride;
                if (prevIdx < 0 || prevIdx >= i)
                {
                    // Degenerate — treat as new bone rather than crashing.
                    boneOf[i] = -1;
                }
                else
                {
                    boneOf[i] = boneOf[prevIdx];
                }
            }
        }

        var result = new List<ThpsRawKey>[numBones];
        for (var b = 0; b < numBones; b++) result[b] = new List<ThpsRawKey>();
        for (var i = 0; i < keys.Length; i++)
        {
            if (boneOf[i] >= 0 && boneOf[i] < numBones)
                result[boneOf[i]].Add(keys[i]);
        }

        var arr = new ThpsRawKey[numBones][];
        for (var b = 0; b < numBones; b++) arr[b] = result[b].ToArray();
        return arr;
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
    ///     Reconstruct unit quaternion W component from X, Y, Z, then conjugate.
    ///     W = sqrt(1 - x² - y² - z²), sign from signBit.
    ///     The THUG engine's QuatVecToMatrix conjugates the quaternion before
    ///     building a rotation matrix — the file stores q but the engine uses q*.
    ///     This matches Ps2SkeletonFile.cs:71 so animation and skeleton live in
    ///     the same convention. Tested: not conjugating produces visibly worse
    ///     motion (sideways/stretched character).
    /// </summary>
    private static Quaternion ReconstructQuat(float x, float y, float z, bool signBit)
    {
        if (x == 0 && y == 0 && z == 0)
            return Quaternion.Identity;

        var sum = 1f - x * x - y * y - z * z;
        var w = sum > 0 ? MathF.Sqrt(sum) : 0f;
        if (signBit) w = -w;
        return Quaternion.Conjugate(new Quaternion(x, y, z, w));
    }

    private readonly record struct ThpsRawKey(float X, float Y, float Z, float W, float Time, int Prev, int RecIndex);

    private enum ThpsRecordKind
    {
        Q,
        T
    }
}
