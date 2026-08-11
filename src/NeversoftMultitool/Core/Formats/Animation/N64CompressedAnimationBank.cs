using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     An animation table embedded in an Edge of Reality N64 model shell.
///     Both table variants use N64 byte order. Compressed <c>0x2C</c> chunks
///     retain the PS1 <c>DecompressStream</c> payload's little-endian s16
///     values, while direct-matrix <c>0x2A</c> chunks store every
///     <c>SMatrix</c> s16 in big-endian order.
/// </summary>
internal sealed class N64CompressedAnimationBank
{
    private const int EntrySize = 8;
    private const int MaxEntries = 4096;
    private const int MaxFrames = 4096;
    private readonly ReadOnlyMemory<byte> _chunk;
    private readonly uint _chunkTag;

    private N64CompressedAnimationBank(
        uint chunkTag,
        ReadOnlyMemory<byte> chunk,
        IReadOnlyList<N64CompressedAnimationEntry> entries)
    {
        _chunkTag = chunkTag;
        _chunk = chunk;
        Entries = entries;
    }

    public uint ChunkTag => _chunkTag;

    public IReadOnlyList<N64CompressedAnimationEntry> Entries { get; }

    /// <summary>
    ///     Finds the last <c>0x2A</c>/<c>0x2C</c> animation chunk using the
    ///     runtime's overwrite semantics and parses that variant's big-endian
    ///     table grammar.
    /// </summary>
    public static N64CompressedAnimationBank? TryParse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!PsxN64ShellFile.IsN64Shell(data)
            || !TryFindLastAnimationChunk(data, out var tag, out var start, out var length))
        {
            return null;
        }

        var chunk = data.AsMemory(start, length);
        var span = chunk.Span;
        if (span.Length < 4)
            return null;

        var count = BinaryPrimitives.ReadUInt32BigEndian(span);
        if (count is 0 or > MaxEntries)
            return null;

        var tableLength64 = 4L + count * EntrySize;
        if (tableLength64 > span.Length)
            return null;
        var tableLength = (int)tableLength64;

        var entries = new List<N64CompressedAnimationEntry>((int)count);
        var previousOffset = -1;
        for (var i = 0; i < count; i++)
        {
            var entryOffset = 4 + i * EntrySize;
            var poolOffsetValue = BinaryPrimitives.ReadUInt32BigEndian(span[entryOffset..]);
            if (poolOffsetValue > int.MaxValue)
                return null;
            var poolOffset = (int)poolOffsetValue;

            // Direct 0x2A:     {u32be dataOff, u16be playbackFrames, u16be tween}
            // Compressed 0x2C: {u32be dataOff, u16be zero,           u16be frames}
            var firstHalfword = BinaryPrimitives.ReadUInt16BigEndian(span[(entryOffset + 4)..]);
            var secondHalfword = BinaryPrimitives.ReadUInt16BigEndian(span[(entryOffset + 6)..]);
            var frameCount = tag == PsxMeshFile.HierChunkV1Tag
                ? firstHalfword
                : secondHalfword;
            var tweenFlag = tag == PsxMeshFile.HierChunkV1Tag
                ? secondHalfword
                : 0;
            if ((tag == PsxMeshFile.HierChunkV2Tag && firstHalfword != 0)
                || frameCount is 0 or > MaxFrames
                || poolOffset < tableLength
                || poolOffset >= span.Length
                || poolOffset <= previousOffset)
            {
                return null;
            }

            entries.Add(new N64CompressedAnimationEntry(poolOffset, frameCount, tweenFlag));
            previousOffset = poolOffset;
        }

        return new N64CompressedAnimationBank(tag, chunk, entries);
    }

    /// <summary>
    ///     Decodes one slot with the matching shared PS1 codec. The owned span
    ///     ends at the next entry's greater pool offset (or the chunk end), so
    ///     malformed data cannot borrow bytes from the following clip.
    /// </summary>
    public PsxAnimation DecodeSlot(int index, int boneCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boneCount);
        if ((uint)index >= (uint)Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var entry = Entries[index];
        var end = index + 1 < Entries.Count
            ? Entries[index + 1].PoolOffset
            : _chunk.Length;
        var available = end - entry.PoolOffset;
        if (available <= 0)
            throw new InvalidDataException($"N64 animation {index} has an empty or reversed payload range.");

        if (_chunkTag == PsxMeshFile.HierChunkV1Tag)
            return DecodeDirectSlot(index, boneCount, entry, available);

        // DecompressStream's bit-window reader peeks up to two bytes beyond
        // the last logically consumed byte. Supply zero sentinels rather than
        // exposing the next clip: the decoder remains physically bounded to
        // this entry and the consumed-count check below remains authoritative.
        var boundedPayload = new byte[available + 2];
        _chunk.Span.Slice(entry.PoolOffset, available).CopyTo(boundedPayload);
        var animation = PsxAnimDecoder.Decode(
            boundedPayload,
            boneCount,
            entry.FrameCount,
            out var consumed);
        if (consumed > available)
        {
            throw new InvalidDataException(
                $"N64 animation {index} crosses its pool bound: consumed {consumed}, available {available}.");
        }

        return animation;
    }

    private PsxAnimation DecodeDirectSlot(
        int index,
        int boneCount,
        N64CompressedAnimationEntry entry,
        int available)
    {
        int required;
        try
        {
            var storedFrames = PsxAnimDecoder.GetDirectMatrixStoredFrameCount(
                entry.FrameCount, entry.TweenFlag);
            required = checked(storedFrames * boneCount * PsxAnimDecoder.DirectMatrixStrideBytes);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                $"N64 direct animation {index} has an overflowing payload size.", ex);
        }

        if (available < required)
        {
            throw new InvalidDataException(
                $"N64 direct animation {index} crosses its pool bound: " +
                $"needs {required}, available {available}.");
        }

        // The N64 port changed only SMatrix cell byte order. Copy exactly the
        // required records (bounded trailing slack is legal), swap each s16,
        // and reuse the independently verified PS1 direct-matrix decoder.
        var littleEndianPayload = new byte[required];
        var source = _chunk.Span.Slice(entry.PoolOffset, required);
        for (var offset = 0; offset < required; offset += 2)
        {
            littleEndianPayload[offset] = source[offset + 1];
            littleEndianPayload[offset + 1] = source[offset];
        }

        return PsxAnimDecoder.DecodeDirectMatrix(
            littleEndianPayload,
            boneCount,
            entry.FrameCount,
            entry.TweenFlag);
    }

    private static bool TryFindLastAnimationChunk(
        byte[] data,
        out uint animationTag,
        out int animationStart,
        out int animationLength)
    {
        animationTag = 0;
        animationStart = -1;
        animationLength = 0;
        if (data.Length < 12)
            return false;

        var metaTopValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (metaTopValue > int.MaxValue)
            return false;
        var cursor = (int)metaTopValue;
        if (cursor < 12 || cursor + 4 > data.Length)
            return false;

        var found = false;
        for (var chunks = 0; chunks < 64; chunks++)
        {
            if (cursor + 4 > data.Length)
                return false;

            var tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor));
            if (tag == uint.MaxValue)
                return found;
            if (cursor + 8 > data.Length)
                return false;

            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4));
            if (lengthValue > int.MaxValue)
                return false;
            var length = (int)lengthValue;
            var start = cursor + 8;
            var end64 = (long)start + length;
            if (end64 > data.Length)
                return false;

            if (tag is PsxMeshFile.HierChunkV1Tag or PsxMeshFile.HierChunkV2Tag)
            {
                animationTag = tag;
                animationStart = start;
                animationLength = length;
                found = true;
            }

            cursor = (int)end64;
        }

        // A valid shell's tagged chain is short and terminates explicitly.
        // Hitting the safety ceiling is malformed, even if an earlier 0x2C
        // happened to look plausible.
        return false;
    }
}

internal readonly record struct N64CompressedAnimationEntry(
    int PoolOffset,
    int FrameCount,
    int TweenFlag);
