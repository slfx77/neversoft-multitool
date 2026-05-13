namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Reads bits MSB-first from a big-endian byte stream.
///     Semantics match Factor 5's M4Decoder bit reader (FUN_802A0834 in the
///     THAW GameCube DOL): given an input sequence of bytes, bits are consumed
///     in reading order, most-significant-bit-first within each byte.
/// </summary>
internal sealed class Vid1BitReader(byte[] data)
{
    private readonly byte[] _data = data;

    public int BytesConsumed => (BitPosition + 7) / 8;

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
        if (bitPosition < 0 || bitPosition > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need absolute position {bitPosition}/{_data.Length * 8}");

        BitPosition = bitPosition;
    }

    public int PeekBits(int bitCount)
    {
        if (bitCount < 0 || BitPosition + bitCount > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {BitPosition}/{_data.Length * 8} (remaining {_data.Length * 8 - BitPosition})");

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
        if (bitCount < 0 || BitPosition + bitCount > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {BitPosition}/{_data.Length * 8} (remaining {_data.Length * 8 - BitPosition})");

        BitPosition += bitCount;
    }

    public int ReadBits(int bitCount)
    {
        if (bitCount < 0 || BitPosition + bitCount > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {BitPosition}/{_data.Length * 8} (remaining {_data.Length * 8 - BitPosition})");

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
        if (BitPosition + bitCount > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {BitPosition}/{_data.Length * 8} (remaining {_data.Length * 8 - BitPosition})");

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
            BitPosition += 8 - (BitPosition & 7);
    }
}
