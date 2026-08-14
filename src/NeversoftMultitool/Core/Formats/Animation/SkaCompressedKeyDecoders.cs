using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Shared THUG-era compressed key grammar: table-lookup / per-component
///     variable-width Q keys and flag-byte T keys (also used by THAW).
/// </summary>
internal static class SkaCompressedKeyDecoders
{
    internal static SkaRotationKey[] DecodeCompressedQKeys(
        ReadOnlySpan<byte> data, ref int off, int end, SkaCompressTable? table)
    {
        ValidateRange(data.Length, off, end, "Q key");
        var keys = new List<SkaRotationKey>();

        while (off < end)
        {
            EnsureAvailable(off, end, 2, "Q key header");
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
                    EnsureAvailable(off, end, 1, "Q lookup index");
                    var index = data[off];
                    off += 1;

                    if (table == null)
                        throw new InvalidDataException(
                            $"SKA compressed Q lookup index {index} requires a Q48 compression table.");

                    qx = table.Q48[index].X / 16384f;
                    qy = table.Q48[index].Y / 16384f;
                    qz = table.Q48[index].Z / 16384f;
                }
                else
                {
                    // Per-component variable encoding
                    timestamp = header & 0x07FF; // 11-bit timestamp

                    if ((header & 0x2000) != 0)
                    {
                        // THUG's get_compressed_q_frame reads through an
                        // unsigned-char pointer and assigns the byte directly
                        // to the s16 component. Values 0x80..0xFF therefore
                        // expand to +128..+255, not -128..-1.
                        EnsureAvailable(off, end, 1, "Q X component");
                        qx = data[off] / 16384f;
                        off += 1;
                    }
                    else
                    {
                        EnsureAvailable(off, end, 2, "Q X component");
                        qx = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                        off += 2;
                    }

                    if ((header & 0x1000) != 0)
                    {
                        EnsureAvailable(off, end, 1, "Q Y component");
                        qy = data[off] / 16384f;
                        off += 1;
                    }
                    else
                    {
                        EnsureAvailable(off, end, 2, "Q Y component");
                        qy = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                        off += 2;
                    }

                    if ((header & 0x0800) != 0)
                    {
                        EnsureAvailable(off, end, 1, "Q Z component");
                        qz = data[off] / 16384f;
                        off += 1;
                    }
                    else
                    {
                        EnsureAvailable(off, end, 2, "Q Z component");
                        qz = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                        off += 2;
                    }
                }
            }
            else
            {
                // Direct: 3 × int16
                timestamp = header & 0x3FFF; // 14-bit timestamp
                EnsureAvailable(off, end, 2, "Q X component");
                qx = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                off += 2;
                EnsureAvailable(off, end, 2, "Q Y component");
                qy = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                off += 2;
                EnsureAvailable(off, end, 2, "Q Z component");
                qz = (short)(data[off] | (data[off + 1] << 8)) / 16384f;
                off += 2;
            }

            var time = timestamp / 60f;
            keys.Add(new SkaRotationKey(time, SkaFile.ReconstructQuat(qx, qy, qz, signBit)));
        }

        return keys.ToArray();
    }

    internal static SkaTranslationKey[] DecodeCompressedTKeys(
        ReadOnlySpan<byte> data, ref int off, int end, SkaCompressTable? table)
    {
        ValidateRange(data.Length, off, end, "T key");
        var keys = new List<SkaTranslationKey>();

        while (off < end)
        {
            EnsureAvailable(off, end, 1, "T key header");
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
                EnsureAvailable(off, end, 2, "T key timestamp");
                timestamp = data[off] | (data[off + 1] << 8);
                off += 2;
            }

            float tx, ty, tz;

            if (useLookup)
            {
                EnsureAvailable(off, end, 1, "T lookup index");
                var index = data[off];
                off += 1;

                if (table == null)
                    throw new InvalidDataException(
                        $"SKA compressed T lookup index {index} requires a T48 compression table.");

                tx = table.T48[index].X / 32f;
                ty = table.T48[index].Y / 32f;
                tz = table.T48[index].Z / 32f;
            }
            else
            {
                // Direct: 3 × int16
                EnsureAvailable(off, end, 2, "T X component");
                tx = (short)(data[off] | (data[off + 1] << 8)) / 32f;
                off += 2;
                EnsureAvailable(off, end, 2, "T Y component");
                ty = (short)(data[off] | (data[off + 1] << 8)) / 32f;
                off += 2;
                EnsureAvailable(off, end, 2, "T Z component");
                tz = (short)(data[off] | (data[off + 1] << 8)) / 32f;
                off += 2;
            }

            var time = timestamp / 60f;
            keys.Add(new SkaTranslationKey(time, new Vector3(tx, ty, tz)));
        }

        return keys.ToArray();
    }

    private static void ValidateRange(int dataLength, int off, int end, string context)
    {
        if (off < 0 || end < off || end > dataLength)
        {
            throw new InvalidDataException(
                $"SKA compressed {context} range [{off}, {end}) is outside the {dataLength}-byte buffer.");
        }
    }

    private static void EnsureAvailable(int off, int end, int count, string context)
    {
        if (count < 0 || off < 0 || end < off || count > end - off)
        {
            throw new InvalidDataException(
                $"SKA compressed {context} at 0x{off:X} crosses the track boundary 0x{end:X}.");
        }
    }
}
