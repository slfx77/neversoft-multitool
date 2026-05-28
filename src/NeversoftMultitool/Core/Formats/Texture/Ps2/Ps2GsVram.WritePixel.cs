namespace NeversoftMultitool.Core.Formats.Texture.Ps2;

internal sealed partial class Ps2GsVram
{
    public void WritePixel(uint dbp, uint dbw, uint dpsm, int x, int y, byte r, byte g, byte b, byte a, uint fbmsk = 0)
    {
        if (x < 0 || y < 0)
            return;

        switch (dpsm)
        {
            case PSMCT32:
            case PSMZ32: // PSMZ32 shares the 32-bit page layout; gsdump replay treats it as color data.
            {
                var addr = GetWordAddressPSMCT32(dbp, dbw, x, y);
                if (addr < VramWords)
                    _vram[addr] = ApplyMask(
                        _vram[addr],
                        r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24),
                        fbmsk);
                break;
            }
            case 0x01: // PSMCT24 uses PSMCT32 addressing with ignored alpha bits.
            case PSMZ24: // PSMZ24 shares the 24-bit page layout; gsdump replay treats it as color data.
            {
                var addr = GetWordAddressPSMCT32(dbp, dbw, x, y);
                if (addr < VramWords)
                    _vram[addr] = ApplyMask(
                        _vram[addr],
                        r | ((uint)g << 8) | ((uint)b << 16),
                        fbmsk | 0xFF000000u);
                break;
            }
            case PSMCT16:
            {
                var (wordAddr, half) = GetWordAddressPSMCT16(dbp, dbw, x, y, false);
                if (wordAddr >= VramWords)
                    break;

                var pixel = (uint)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10) | (a >= 128 ? 0x8000 : 0));
                var mask = fbmsk & 0xFFFFu;
                if (half == 0)
                    _vram[wordAddr] = (_vram[wordAddr] & 0xFFFF0000) |
                                      ApplyMask(_vram[wordAddr] & 0xFFFFu, pixel, mask);
                else
                    _vram[wordAddr] = (_vram[wordAddr] & 0x0000FFFF) |
                                      (ApplyMask((_vram[wordAddr] >> 16) & 0xFFFFu, pixel, mask) << 16);
                break;
            }
            case PSMCT16S:
            {
                var (wordAddr, half) = GetWordAddressPSMCT16(dbp, dbw, x, y, true);
                if (wordAddr >= VramWords)
                    break;

                var pixel = (uint)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10) | (a >= 128 ? 0x8000 : 0));
                var mask = fbmsk & 0xFFFFu;
                if (half == 0)
                    _vram[wordAddr] = (_vram[wordAddr] & 0xFFFF0000) |
                                      ApplyMask(_vram[wordAddr] & 0xFFFFu, pixel, mask);
                else
                    _vram[wordAddr] = (_vram[wordAddr] & 0x0000FFFF) |
                                      (ApplyMask((_vram[wordAddr] >> 16) & 0xFFFFu, pixel, mask) << 16);
                break;
            }
        }
    }

    public (byte R, byte G, byte B, byte A) ReadPixelRgba(uint dbp, uint dbw, uint dpsm, int x, int y)
    {
        if (x < 0 || y < 0)
            return (0, 0, 0, 0);

        switch (dpsm)
        {
            case PSMCT32:
            case PSMZ32:
            case 0x01:
            case PSMZ24:
            {
                var addr = GetWordAddressPSMCT32(dbp, dbw, x, y);
                var word = addr < VramWords ? _vram[addr] : 0u;
                // PSMCT24 has no native alpha; return PS2-nominal full (128) so downstream
                // /128 blend factors evaluate to 1.0. PSMCT32 returns the raw stored byte.
                var alpha = dpsm is 0x01 or PSMZ24 ? 128 : (int)(word >> 24);
                return ((byte)word, (byte)(word >> 8), (byte)(word >> 16), (byte)alpha);
            }
            case PSMCT16:
            case PSMCT16S:
            {
                var (wordAddr, half) = GetWordAddressPSMCT16(dbp, dbw, x, y, dpsm == PSMCT16S);
                var word = wordAddr < VramWords ? _vram[wordAddr] : 0u;
                var pixel = half == 0 ? (ushort)(word & 0xFFFF) : (ushort)(word >> 16);
                return (Expand5(pixel & 0x1F), Expand5((pixel >> 5) & 0x1F), Expand5((pixel >> 10) & 0x1F),
                    (pixel & 0x8000) != 0 ? (byte)255 : (byte)0);
            }
            default:
                return (0, 0, 0, 0);
        }
    }
}
