namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     Reads bits MSB-first from a big-endian byte stream.
///     Semantics match Factor 5's M4Decoder bit reader (FUN_802A0834 in the
///     THAW GameCube DOL): given an input sequence of bytes, bits are consumed
///     in reading order, most-significant-bit-first within each byte.
/// </summary>
internal sealed class Vid1BitReader(byte[] data)
{
    private readonly byte[] _data = data;

    public int BytesConsumed => (int)(((long)BitPosition + 7) / 8);

    public int BitPosition { get; private set; }

    public Vid1BitReader Clone()
    {
        var clone = new Vid1BitReader(_data);
        clone.BitPosition = BitPosition;
        return clone;
    }

    public void Restore(Vid1BitReader snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(_data, snapshot._data))
            throw new InvalidOperationException("VID1 bit reader snapshot targets a different buffer");

        BitPosition = snapshot.BitPosition;
    }

    public void SetBitPosition(int bitPosition)
    {
        var totalBits = (long)_data.Length * 8;
        if (bitPosition < 0 || bitPosition > totalBits)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need absolute position {bitPosition}/{totalBits}");

        BitPosition = bitPosition;
    }

    public int PeekBits(int bitCount)
    {
        EnsureAvailable(bitCount);

        var value = 0;
        var pos = BitPosition;
        for (var i = 0; i < bitCount; i++)
        {
            var byteIndex = pos >> 3;
            var bitIndex = 7 - (pos & 7);
            value = (value << 1) | ((_data[byteIndex] >> bitIndex) & 1);
            pos++;
        }

        return value;
    }

    public void SkipBits(int bitCount)
    {
        BitPosition = EnsureAvailable(bitCount);
    }

    public int ReadBits(int bitCount)
    {
        EnsureAvailable(bitCount);

        var value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            var byteIndex = BitPosition >> 3;
            var bitIndex = 7 - (BitPosition & 7);
            value = (value << 1) | ((_data[byteIndex] >> bitIndex) & 1);
            BitPosition++;
        }

        return value;
    }

    public uint ReadBitsUInt32()
    {
        const int bitCount = 32;
        EnsureAvailable(bitCount);

        uint value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            var byteIndex = BitPosition >> 3;
            var bitIndex = 7 - (BitPosition & 7);
            value = (value << 1) | (uint)((_data[byteIndex] >> bitIndex) & 1);
            BitPosition++;
        }

        return value;
    }

    public bool ReadFlag()
    {
        return ReadBits(1) != 0;
    }

    public void AlignToNextByte()
    {
        if ((BitPosition & 7) != 0)
            SkipBits(8 - (BitPosition & 7));
    }

    private int EnsureAvailable(int bitCount)
    {
        var totalBits = (long)_data.Length * 8;
        var endPosition = (long)BitPosition + bitCount;
        if (bitCount < 0 || BitPosition < 0 || endPosition > totalBits || endPosition > int.MaxValue)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {BitPosition}/{totalBits} " +
                $"(remaining {totalBits - BitPosition})");

        return (int)endPosition;
    }
}
