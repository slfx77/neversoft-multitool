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
    private int _bitPosition;

    public int BytesConsumed => (_bitPosition + 7) / 8;

    public int BitPosition => _bitPosition;

    public Vid1BitReader Clone()
    {
        var clone = new Vid1BitReader(_data);
        clone._bitPosition = _bitPosition;
        return clone;
    }

    public void Restore(Vid1BitReader snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(_data, snapshot._data))
            throw new InvalidOperationException("VID1 bit reader snapshot targets a different buffer");

        _bitPosition = snapshot._bitPosition;
    }

    public void SetBitPosition(int bitPosition)
    {
        if (bitPosition < 0 || bitPosition > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need absolute position {bitPosition}/{_data.Length * 8}");

        _bitPosition = bitPosition;
    }

    public int PeekBits(int bitCount)
    {
        if (bitCount < 0 || _bitPosition + bitCount > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {_bitPosition}/{_data.Length * 8} (remaining {_data.Length * 8 - _bitPosition})");

        var value = 0;
        var pos = _bitPosition;
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
        if (bitCount < 0 || _bitPosition + bitCount > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {_bitPosition}/{_data.Length * 8} (remaining {_data.Length * 8 - _bitPosition})");

        _bitPosition += bitCount;
    }

    public int ReadBits(int bitCount)
    {
        if (bitCount < 0 || _bitPosition + bitCount > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {_bitPosition}/{_data.Length * 8} (remaining {_data.Length * 8 - _bitPosition})");

        var value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            var byteIndex = _bitPosition >> 3;
            var bitIndex = 7 - (_bitPosition & 7);
            value = (value << 1) | ((_data[byteIndex] >> bitIndex) & 1);
            _bitPosition++;
        }

        return value;
    }

    public uint ReadBitsUInt32()
    {
        const int bitCount = 32;
        if (_bitPosition + bitCount > _data.Length * 8)
            throw new EndOfStreamException(
                $"VID1 bitstream is truncated: need {bitCount} bits at pos {_bitPosition}/{_data.Length * 8} (remaining {_data.Length * 8 - _bitPosition})");

        uint value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            var byteIndex = _bitPosition >> 3;
            var bitIndex = 7 - (_bitPosition & 7);
            value = (value << 1) | (uint)((_data[byteIndex] >> bitIndex) & 1);
            _bitPosition++;
        }

        return value;
    }

    public bool ReadFlag()
    {
        return ReadBits(1) != 0;
    }

    public void AlignToNextByte()
    {
        if ((_bitPosition & 7) != 0)
            _bitPosition += 8 - (_bitPosition & 7);
    }
}
