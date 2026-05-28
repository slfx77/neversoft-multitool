using static NeversoftMultitool.Core.Formats.Texture.Ps2.Ps2GsVramTables;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2;

/// <summary>
///     Simulates PS2 GS VRAM for cross-format texture decoding.
///     When texture data is DMA-uploaded in one pixel format (e.g. PSMCT32) but read
///     as another (e.g. PSMT4), the data must be written to a VRAM buffer using the
///     upload format's page/block/column layout, then read back using the texture
///     format's layout. This class implements the GS VRAM addressing for all
///     relevant pixel storage modes.
///     GS VRAM organization (from GS User's Manual):
///     - VRAM is 4MB of 32-bit words (1M words)
///     - Address unit: 256-byte blocks (64 words)
///     - Each block address (DBP/TBP) × 64 = word offset
///     - Pages contain 32 blocks; page size in pixels varies by PSM
///     - Within pages: blocks arranged in PSM-specific pattern
///     - Within blocks: columns of pixels with PSM-specific word mapping
/// </summary>
internal sealed partial class Ps2GsVram
{
    // GS VRAM is 4MB = 1M 32-bit words
    private const int VramWords = 1024 * 1024;

    // PSM constants
    internal const uint PSMCT32 = 0x00;
    internal const uint PSMCT16 = 0x02;
    internal const uint PSMCT16S = 0x0A;
    internal const uint PSMT8 = 0x13;
    internal const uint PSMT4 = 0x14;
    internal const uint PSMZ32 = 0x30;
    internal const uint PSMZ24 = 0x31;

    private readonly Ps2GifQwordWordOrder _gifQwordWordOrder;
    private readonly uint[] _vram = new uint[VramWords];

    public Ps2GsVram() : this(Ps2GifQwordWordOrder.Identity)
    {
    }

    public Ps2GsVram(Ps2GifQwordWordOrder gifQwordWordOrder)
    {
        _gifQwordWordOrder = gifQwordWordOrder;
    }

    /// <summary>
    ///     Write a rectangular region to VRAM using PSMCT32 addressing.
    ///     Each pixel is 4 bytes (32 bits) in the source data.
    /// </summary>
    public void WriteRectPSMCT32(uint dbp, uint dbw, int rrw, int rrh, ReadOnlySpan<byte> data)
    {
        WriteRectPSMCT32(dbp, dbw, rrw, rrh, data, 0, 0);
    }

    public void WriteRectPSMCT32(uint dbp, uint dbw, int rrw, int rrh, ReadOnlySpan<byte> data, int dsax, int dsay)
    {
        var srcOff = 0;
        for (var y = 0; y < rrh; y++)
        {
            for (var x = 0; x < rrw; x++)
            {
                if (srcOff + 4 > data.Length) return;
                var mappedOff = MapWordOffset(srcOff, data.Length);
                var word = (uint)(data[mappedOff] | (data[mappedOff + 1] << 8) |
                                  (data[mappedOff + 2] << 16) | (data[mappedOff + 3] << 24));
                srcOff += 4;

                var addr = GetWordAddressPSMCT32(dbp, dbw, dsax + x, dsay + y);
                if (addr < VramWords)
                    _vram[addr] = word;
            }
        }
    }

    /// <summary>
    ///     Write a rectangular region to VRAM using PSMCT16 addressing.
    ///     Each pixel is 2 bytes (16 bits) in the source data.
    /// </summary>
    public void WriteRectPSMCT16(uint dbp, uint dbw, int rrw, int rrh, ReadOnlySpan<byte> data)
    {
        WriteRectPSMCT16(dbp, dbw, rrw, rrh, data, 0, 0, false);
    }

    public void WriteRectPSMCT16S(uint dbp, uint dbw, int rrw, int rrh, ReadOnlySpan<byte> data)
    {
        WriteRectPSMCT16(dbp, dbw, rrw, rrh, data, 0, 0, true);
    }

    public void WriteRectPSMCT16(uint dbp, uint dbw, int rrw, int rrh, ReadOnlySpan<byte> data, int dsax, int dsay)
    {
        WriteRectPSMCT16(dbp, dbw, rrw, rrh, data, dsax, dsay, false);
    }

    public void WriteRectPSMCT16S(uint dbp, uint dbw, int rrw, int rrh, ReadOnlySpan<byte> data, int dsax, int dsay)
    {
        WriteRectPSMCT16(dbp, dbw, rrw, rrh, data, dsax, dsay, true);
    }

    private void WriteRectPSMCT16(
        uint dbp,
        uint dbw,
        int rrw,
        int rrh,
        ReadOnlySpan<byte> data,
        int dsax,
        int dsay,
        bool signedMode)
    {
        var srcOff = 0;
        for (var y = 0; y < rrh; y++)
        {
            for (var x = 0; x < rrw; x++)
            {
                if (srcOff + 2 > data.Length) return;
                var mappedOff = MapHalfwordOffset(srcOff, data.Length);
                var halfword = (uint)(data[mappedOff] | (data[mappedOff + 1] << 8));
                srcOff += 2;

                var (wordAddr, half) = GetWordAddressPSMCT16(dbp, dbw, dsax + x, dsay + y, signedMode);
                if (wordAddr < VramWords)
                {
                    if (half == 0)
                        _vram[wordAddr] = (_vram[wordAddr] & 0xFFFF0000) | halfword;
                    else
                        _vram[wordAddr] = (_vram[wordAddr] & 0x0000FFFF) | (halfword << 16);
                }
            }
        }
    }

    /// <summary>
    ///     Write a rectangular region using the specified PSM format.
    /// </summary>
    public void WriteRect(uint dbp, uint dbw, uint dpsm, int rrw, int rrh, ReadOnlySpan<byte> data)
    {
        WriteRect(dbp, dbw, dpsm, rrw, rrh, data, 0, 0);
    }

    public void WriteRect(uint dbp, uint dbw, uint dpsm, int rrw, int rrh, ReadOnlySpan<byte> data, int dsax, int dsay)
    {
        switch (dpsm)
        {
            case PSMCT32:
            case PSMZ32:
            case PSMZ24:
                WriteRectPSMCT32(dbp, dbw, rrw, rrh, data, dsax, dsay);
                break;
            case PSMCT16:
                WriteRectPSMCT16(dbp, dbw, rrw, rrh, data, dsax, dsay);
                break;
            case PSMCT16S:
                WriteRectPSMCT16S(dbp, dbw, rrw, rrh, data, dsax, dsay);
                break;
            case PSMT8:
                WriteRectPSMT8(dbp, dbw, rrw, rrh, data, dsax, dsay);
                break;
            case PSMT4:
                WriteRectPSMT4(dbp, dbw, rrw, rrh, data, dsax, dsay);
                break;
        }
    }

    private static uint ApplyMask(uint oldValue, uint newValue, uint mask)
    {
        return (oldValue & mask) | (newValue & ~mask);
    }

    private static byte Expand5(int value)
    {
        return (byte)((value << 3) | (value >> 2));
    }

    /// <summary>
    ///     Read a PSMT4 texture from VRAM as linear 4-bit pixel data (packed 2 pixels/byte).
    /// </summary>
    public byte[] ReadTexturePSMT4(uint tbp, uint tbw, int tw, int th)
    {
        var output = new byte[tw * th / 2];
        for (var y = 0; y < th; y++)
        {
            for (var x = 0; x < tw; x++)
            {
                var nibble = ReadNibblePSMT4(tbp, tbw, x, y);
                var byteIdx = (y * tw + x) / 2;
                var shift = ((y * tw + x) & 1) * 4;
                output[byteIdx] = (byte)((output[byteIdx] & ~(0xF << shift)) | ((nibble & 0xF) << shift));
            }
        }

        return output;
    }

    /// <summary>
    ///     Read a PSMT8 texture from VRAM as linear 8-bit pixel data.
    /// </summary>
    public byte[] ReadTexturePSMT8(uint tbp, uint tbw, int tw, int th)
    {
        var output = new byte[tw * th];
        for (var y = 0; y < th; y++)
        {
            for (var x = 0; x < tw; x++)
                output[y * tw + x] = ReadBytePSMT8(tbp, tbw, x, y);
        }

        return output;
    }

    /// <summary>
    ///     Read a PSMCT32 region from VRAM (used for CLUT data).
    /// </summary>
    public byte[] ReadRectPSMCT32(uint cbp, uint cbw, int width, int height)
    {
        var output = new byte[width * height * 4];
        var off = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var addr = GetWordAddressPSMCT32(cbp, cbw, x, y);
                var word = addr < VramWords ? _vram[addr] : 0u;
                output[off++] = (byte)word;
                output[off++] = (byte)(word >> 8);
                output[off++] = (byte)(word >> 16);
                output[off++] = (byte)(word >> 24);
            }
        }

        return output;
    }

    private int MapWordOffset(int srcOff, int dataLength)
    {
        if (_gifQwordWordOrder.IsIdentity)
            return srcOff;

        var qwordBase = srcOff & ~0xF;
        var wordIndex = (srcOff >> 2) & 0x3;
        var mappedOff = qwordBase + _gifQwordWordOrder.MapWord(wordIndex) * 4;
        return mappedOff + 4 <= dataLength ? mappedOff : srcOff;
    }

    private int MapHalfwordOffset(int srcOff, int dataLength)
    {
        if (_gifQwordWordOrder.IsIdentity)
            return srcOff;

        var qwordBase = srcOff & ~0xF;
        var halfwordIndex = (srcOff >> 1) & 0x7;
        var wordIndex = halfwordIndex >> 1;
        var halfInWord = halfwordIndex & 0x1;
        var mappedOff = qwordBase + _gifQwordWordOrder.MapWord(wordIndex) * 4 + halfInWord * 2;
        return mappedOff + 2 <= dataLength ? mappedOff : srcOff;
    }

    /// <summary>
    ///     Read a PSMCT16 region from VRAM (used for 16-bit CLUT data).
    /// </summary>
    public byte[] ReadRectPSMCT16(uint cbp, uint cbw, int width, int height)
    {
        return ReadRectPSMCT16(cbp, cbw, width, height, false);
    }

    public byte[] ReadRectPSMCT16S(uint cbp, uint cbw, int width, int height)
    {
        return ReadRectPSMCT16(cbp, cbw, width, height, true);
    }

    private byte[] ReadRectPSMCT16(uint cbp, uint cbw, int width, int height, bool signedMode)
    {
        var output = new byte[width * height * 2];
        var off = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (wordAddr, half) = GetWordAddressPSMCT16(cbp, cbw, x, y, signedMode);
                var word = wordAddr < VramWords ? _vram[wordAddr] : 0u;
                var pixel = half == 0 ? (ushort)(word & 0xFFFF) : (ushort)(word >> 16);
                output[off++] = (byte)pixel;
                output[off++] = (byte)(pixel >> 8);
            }
        }

        return output;
    }

    public uint[] ReadRawBlockWords(uint bp, int wordCount)
    {
        var output = new uint[wordCount];
        var baseAddr = (int)(bp * 64);
        for (var i = 0; i < wordCount; i++)
        {
            var addr = baseAddr + i;
            output[i] = addr >= 0 && addr < VramWords ? _vram[addr] : 0u;
        }

        return output;
    }

    public ushort[] ReadRawBlockHalfwords(uint bp, int halfwordCount)
    {
        var output = new ushort[halfwordCount];
        var wordCount = (halfwordCount + 1) / 2;
        var words = ReadRawBlockWords(bp, wordCount);

        var off = 0;
        foreach (var word in words)
        {
            if (off < output.Length)
                output[off++] = (ushort)word;
            if (off < output.Length)
                output[off++] = (ushort)(word >> 16);
        }

        return output;
    }

    /// <summary>
    ///     Copy raw GS local-memory bytes directly into VRAM words starting at the given block pointer.
    ///     Useful for diagnostics when file data may already be stored in GS-native page layout.
    /// </summary>
    public void WriteRawBytes(uint bp, ReadOnlySpan<byte> data)
    {
        var baseAddr = (int)(bp * 64);
        var wordCount = Math.Min((data.Length + 3) / 4, VramWords - baseAddr);
        for (var i = 0; i < wordCount; i++)
        {
            var srcOff = i * 4;
            uint word = 0;
            if (srcOff < data.Length) word |= data[srcOff];
            if (srcOff + 1 < data.Length) word |= (uint)data[srcOff + 1] << 8;
            if (srcOff + 2 < data.Length) word |= (uint)data[srcOff + 2] << 16;
            if (srcOff + 3 < data.Length) word |= (uint)data[srcOff + 3] << 24;
            _vram[baseAddr + i] = word;
        }
    }

    // ---- PSMCT32 addressing ----

}
