using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

internal static partial class SkaFile
{
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
}
