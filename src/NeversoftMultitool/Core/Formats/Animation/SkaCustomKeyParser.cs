namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Strict reader for the little-endian custom-event records used by the
///     version-1 THPS4 platform and compression-table SKA layouts.
/// </summary>
internal static class SkaCustomKeyParser
{
    internal static SkaCustomKey[] ParseLittleEndianExact(
        ReadOnlySpan<byte> data,
        int offset,
        uint rawCount,
        string context,
        bool allowTerminalAlignmentPadding = false)
    {
        if (rawCount > int.MaxValue)
            throw new InvalidDataException($"{context}: custom-key count {rawCount} is out of range");

        var count = (int)rawCount;
        var keys = new SkaCustomKey[count];
        for (var i = 0; i < keys.Length; i++)
        {
            if (offset < 0 || offset > data.Length - 12)
            {
                throw new InvalidDataException(
                    $"{context}: custom key {i} header overruns file at 0x{offset:X}");
            }

            var timestamp = BitConverter.ToUInt32(data[offset..]);
            var type = BitConverter.ToUInt32(data[(offset + 4)..]);
            var size = BitConverter.ToUInt32(data[(offset + 8)..]);
            if (size < 12)
            {
                throw new InvalidDataException(
                    $"{context}: custom key {i} record size {size} is smaller than its 12-byte header");
            }

            if ((size & 3) != 0)
            {
                throw new InvalidDataException(
                    $"{context}: custom key {i} record size {size} is not four-byte aligned");
            }

            var end = (long)offset + size;
            if (end > data.Length)
            {
                throw new InvalidDataException(
                    $"{context}: custom key {i} end 0x{end:X} exceeds file length 0x{data.Length:X}");
            }

            if ((type == 1 || type == 4) && size != 16)
            {
                throw new InvalidDataException(
                    $"{context}: decoded custom-key type {type} must be a 16-byte record (size {size})");
            }

            var payloadLength = checked((int)size - 12);
            var payload = data.Slice(offset + 12, payloadLength).ToArray();
            float? fov = type == 1 ? BitConverter.ToSingle(data[(offset + 12)..]) : null;
            if (fov.HasValue && !float.IsFinite(fov.Value))
            {
                throw new InvalidDataException(
                    $"{context}: custom key {i} contains a non-finite camera field of view");
            }

            keys[i] = new SkaCustomKey
            {
                Timestamp = timestamp,
                Type = type,
                Size = size,
                Payload = payload,
                Fov = fov,
                ScriptQbKey = type == 4 ? BitConverter.ToUInt32(data[(offset + 12)..]) : null
            };

            offset = checked((int)end);
        }

        if (offset != data.Length &&
            (!allowTerminalAlignmentPadding || !IsZeroAlignmentPadding(data, offset)))
        {
            throw new InvalidDataException(
                $"{context}: custom keys end at 0x{offset:X}, but file length is 0x{data.Length:X}");
        }

        return keys;
    }

    private static bool IsZeroAlignmentPadding(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset > data.Length)
            return false;

        var aligned = ((long)offset + 3) & ~3L;
        if (aligned != data.Length)
            return false;

        for (var i = offset; i < data.Length; i++)
        {
            if (data[i] != 0)
                return false;
        }

        return true;
    }
}
