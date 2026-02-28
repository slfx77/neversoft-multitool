namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Reads bits MSB-first from data stored in 16-bit little-endian word order.
///     STR v2 convention: bytes are read as 16-bit LE words, bits within each word are MSB-first.
/// </summary>
internal ref struct MdecBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;
    private readonly int _totalBits;

    public MdecBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPos = 0;
        _totalBits = data.Length * 8;
    }

    public bool IsExhausted => _bitPos >= _totalBits;

    /// <summary>
    ///     Reads a 16-bit word at the given byte offset in STR v2 byte order (byte-swapped LE).
    ///     Returns the word with MSB-first bit ordering for extraction.
    /// </summary>
    private ushort ReadWord(int byteOffset)
    {
        // STR v2: bytes within each 16-bit word are swapped (XOR 1)
        // Word at byte offset N: high byte = data[N^1], low byte = data[(N+1)^1]
        // For aligned word at offset 0: high = data[1], low = data[0] -> big-endian read of LE data
        var alignedBase = byteOffset & ~1;
        if (alignedBase + 1 < _data.Length)
            return (ushort)((_data[alignedBase + 1] << 8) | _data[alignedBase]);
        if (alignedBase < _data.Length)
            return (ushort)(_data[alignedBase] & 0xFF);
        return 0;
    }

    public uint PeekBits(int count)
    {
        if (_bitPos >= _totalBits) return 0;

        // Build a 32-bit buffer from the current position
        var bytePos = _bitPos >> 3;
        var bitOffset = _bitPos & 15; // bit offset within current 16-bit word
        var wordBase = bytePos & ~1; // align to 16-bit word boundary

        // Read up to 3 consecutive words to get enough bits
        uint buf = ReadWord(wordBase);
        buf = (buf << 16) | ReadWord(wordBase + 2);

        // We have 32 bits starting from wordBase. Shift to align with our bit position.
        // Our bit is at offset bitOffset within the first word.
        buf <<= bitOffset;

        // If we need more than (32 - bitOffset) bits, read another word
        if (bitOffset + count > 32)
        {
            uint extra = ReadWord(wordBase + 4);
            buf |= extra >> (16 - bitOffset);
        }

        return buf >> (32 - count);
    }

    public uint ReadBits(int count)
    {
        var result = PeekBits(count);
        _bitPos += count;
        return result;
    }

    public int ReadSignedBits(int count)
    {
        var val = (int)ReadBits(count);
        // Sign-extend
        var signBit = 1 << (count - 1);
        if ((val & signBit) != 0)
            val |= ~((1 << count) - 1);
        return val;
    }

    public void SkipBits(int count)
    {
        _bitPos += count;
    }
}
