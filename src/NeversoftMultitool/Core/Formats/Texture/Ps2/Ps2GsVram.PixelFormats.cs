using static NeversoftMultitool.Core.Formats.Texture.Ps2.Ps2GsVramTables;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2;

internal sealed partial class Ps2GsVram
{
    private static int GetWordAddressPSMCT32(uint dbp, uint dbw, int x, int y)
    {
        var pageX = x / 64;
        var pageY = y / 32;
        var page = (int)(pageX + pageY * dbw);

        var blockX = x % 64 / 8;
        var blockY = y % 32 / 8;
        var block = Block32[blockY * 8 + blockX];

        var word = ColumnOffset32[y % 8, x % 8];

        return (int)(dbp * 64) + page * 2048 + block * 64 + word;
    }

    // ---- PSMCT16 addressing ----

    private static (int wordAddr, int half) GetWordAddressPSMCT16(uint dbp, uint dbw, int x, int y, bool signedMode)
    {
        var pageX = x / 64;
        var pageY = y / 64;
        var page = (int)(pageX + pageY * dbw);

        var blockX = x % 64 / 16;
        var blockY = y % 64 / 8;
        var block = (signedMode ? Block16S : Block16)[blockY * 4 + blockX];

        var colY = y % 8;
        var column = colY / 2;
        var cy = colY % 2;
        var cx = x % 16;
        var word = ColumnWord16[cx + cy * 16];
        var half = ColumnHalf16[cx + cy * 16];

        var addr = (int)(dbp * 64) + page * 2048 + block * 64 + column * 16 + word;
        return (addr, half);
    }

    // ---- PSMT4 addressing ----

    private int ReadNibblePSMT4(uint tbp, uint tbw, int x, int y)
    {
        var pageX = x / 128;
        var pageY = y / 128;
        var page = (int)(pageX + pageY * (tbw >> 1));

        var blockX = x % 128 / 32;
        var blockY = y % 128 / 16;
        var block = Block4[blockY * 4 + blockX];

        var offset = ColumnOffset4[y % 16, x % 32];
        var addr = (int)(tbp * 64) + page * 2048 + block * 64 + (offset >> 3);
        if (addr < 0 || addr >= VramWords) return 0;

        var vramWord = _vram[addr];
        var shift = (offset & 0x7) * 4;
        return (int)((vramWord >> shift) & 0xF);
    }

    // ---- PSMT8 addressing ----

    private byte ReadBytePSMT8(uint tbp, uint tbw, int x, int y)
    {
        var pageX = x / 128;
        var pageY = y / 64;
        var page = (int)(pageX + pageY * (tbw >> 1));

        var blockX = x % 128 / 16;
        var blockY = y % 64 / 16;
        var block = Block8[blockY * 8 + blockX];

        var offset = ColumnOffset8[y % 16, x % 16];
        var addr = (int)(tbp * 64) + page * 2048 + block * 64 + (offset >> 2);
        if (addr < 0 || addr >= VramWords) return 0;

        var vramWord = _vram[addr];
        return (byte)((vramWord >> ((offset & 0x3) * 8)) & 0xFF);
    }

    // ---- PSMT4 write (for rare PSMT4-format uploads) ----

    private void WriteRectPSMT4(uint dbp, uint dbw, int rrw, int rrh, ReadOnlySpan<byte> data, int dsax, int dsay)
    {
        for (var y = 0; y < rrh; y++)
        {
            for (var x = 0; x < rrw; x++)
            {
                var pixelIdx = y * rrw + x;
                var byteIdx = pixelIdx / 2;
                if (byteIdx >= data.Length) return;
                var nibble = (data[byteIdx] >> ((pixelIdx & 1) * 4)) & 0xF;
                WriteNibblePSMT4(dbp, dbw, dsax + x, dsay + y, nibble);
            }
        }
    }

    private void WriteNibblePSMT4(uint dbp, uint dbw, int x, int y, int nibble)
    {
        var pageX = x / 128;
        var pageY = y / 128;
        var page = (int)(pageX + pageY * (dbw >> 1));

        var blockX = x % 128 / 32;
        var blockY = y % 128 / 16;
        var block = Block4[blockY * 4 + blockX];

        var offset = ColumnOffset4[y % 16, x % 32];
        var addr = (int)(dbp * 64) + page * 2048 + block * 64 + (offset >> 3);
        if (addr < 0 || addr >= VramWords) return;

        var shift = (offset & 0x7) * 4;
        _vram[addr] = (_vram[addr] & ~(0xFu << shift)) | ((uint)(nibble & 0xF) << shift);
    }

    // ---- PSMT8 write (for rare PSMT8-format uploads) ----

    private void WriteRectPSMT8(uint dbp, uint dbw, int rrw, int rrh, ReadOnlySpan<byte> data, int dsax, int dsay)
    {
        var srcOff = 0;
        for (var y = 0; y < rrh; y++)
        {
            for (var x = 0; x < rrw; x++)
            {
                if (srcOff >= data.Length) return;
                WriteBytePSMT8(dbp, dbw, dsax + x, dsay + y, data[srcOff++]);
            }
        }
    }

    private void WriteBytePSMT8(uint dbp, uint dbw, int x, int y, byte value)
    {
        var pageX = x / 128;
        var pageY = y / 64;
        var page = (int)(pageX + pageY * (dbw >> 1));

        var blockX = x % 128 / 16;
        var blockY = y % 64 / 16;
        var block = Block8[blockY * 8 + blockX];

        var offset = ColumnOffset8[y % 16, x % 16];
        var addr = (int)(dbp * 64) + page * 2048 + block * 64 + (offset >> 2);
        if (addr < 0 || addr >= VramWords) return;

        var shift = (offset & 0x3) * 8;
        _vram[addr] = (_vram[addr] & ~(0xFFu << shift)) | ((uint)value << shift);
    }
}
