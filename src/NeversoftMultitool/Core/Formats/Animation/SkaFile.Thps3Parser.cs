using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

internal static partial class SkaFile
{
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
}
