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
            {
                var addr = GetWordAddressPSMCT32(dbp, dbw, x, y);
                if (addr < VramWords)
                    _vram[addr] = ApplyMask(
                        _vram[addr],
                        r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24),
                        fbmsk);
                break;
            }
            case PSMZ32:
            {
                var addr = GetWordAddressPSMZ32(dbp, dbw, x, y);
                if (addr < VramWords)
                    _vram[addr] = ApplyMask(
                        _vram[addr],
                        r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24),
                        fbmsk);
                break;
            }
            case 0x01: // PSMCT24 uses PSMCT32 addressing with ignored alpha bits.
            {
                var addr = GetWordAddressPSMCT32(dbp, dbw, x, y);
                if (addr < VramWords)
                    _vram[addr] = ApplyMask(
                        _vram[addr],
                        r | ((uint)g << 8) | ((uint)b << 16),
                        fbmsk | 0xFF000000u);
                break;
            }
            case PSMZ24:
            {
                var addr = GetWordAddressPSMZ32(dbp, dbw, x, y);
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
                WritePsmct16Word(wordAddr, half, r, g, b, a, fbmsk);
                break;
            }
            case PSMCT16S:
            {
                var (wordAddr, half) = GetWordAddressPSMCT16(dbp, dbw, x, y, true);
                WritePsmct16Word(wordAddr, half, r, g, b, a, fbmsk);
                break;
            }
            case PSMZ16:
            {
                var (wordAddr, half) = GetWordAddressPSMZ16(dbp, dbw, x, y, false);
                WriteRawHalfword(wordAddr, half, (uint)(r | (g << 8)), fbmsk);
                break;
            }
            case PSMZ16S:
            {
                var (wordAddr, half) = GetWordAddressPSMZ16(dbp, dbw, x, y, true);
                WriteRawHalfword(wordAddr, half, (uint)(r | (g << 8)), fbmsk);
                break;
            }
        }
    }

    private void WritePsmct16Word(int wordAddr, int half, byte r, byte g, byte b, byte a, uint fbmsk)
    {
        if (wordAddr >= VramWords)
            return;

        var pixel = (uint)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10) | (a >= 128 ? 0x8000 : 0));
        WriteRawHalfword(wordAddr, half, pixel, fbmsk);
    }

    /// <summary>
    ///     Write a raw 16-bit value to one half of a VRAM word, applying the per-halfword
    ///     framebuffer mask. Used by PSMCT16/PSMCT16S (with 5-5-5-1 RGB packing done by the
    ///     caller) and by PSMZ16/PSMZ16S (where the caller passes raw Z bytes via (r, g) —
    ///     PCSX2 GSLocalMemory WritePixel16Z stores the low 16 bits of Z verbatim, no
    ///     5-bit quantization).
    /// </summary>
    private void WriteRawHalfword(int wordAddr, int half, uint halfword, uint fbmsk)
    {
        if (wordAddr >= VramWords)
            return;

        halfword &= 0xFFFFu;
        var mask = fbmsk & 0xFFFFu;
        if (half == 0)
            _vram[wordAddr] = (_vram[wordAddr] & 0xFFFF0000) |
                              ApplyMask(_vram[wordAddr] & 0xFFFFu, halfword, mask);
        else
            _vram[wordAddr] = (_vram[wordAddr] & 0x0000FFFF) |
                              (ApplyMask((_vram[wordAddr] >> 16) & 0xFFFFu, halfword, mask) << 16);
    }

    public (byte R, byte G, byte B, byte A) ReadPixelRgba(uint dbp, uint dbw, uint dpsm, int x, int y, ulong texa = 0)
    {
        if (x < 0 || y < 0)
            return (0, 0, 0, 0);

        switch (dpsm)
        {
            case PSMCT32:
            case 0x01:
            {
                var addr = GetWordAddressPSMCT32(dbp, dbw, x, y);
                var word = addr < VramWords ? _vram[addr] : 0u;
                // PSMCT24 writes mask out alpha (fbmsk | 0xFF000000u), preserving whatever
                // byte a prior PSMCT32 write left there. Cd reads during ABE blending must
                // see that real byte — bloom feedback aliases the same FBP between PSMCT32
                // and PSMCT24 (e.g. THAW FBP=13632), so returning a constant 128 here
                // destroys mid-tone alpha and bimodalises the cascade to 0/128.
                return ((byte)word, (byte)(word >> 8), (byte)(word >> 16), (byte)(word >> 24));
            }
            case PSMZ32:
            case PSMZ24:
            {
                var addr = GetWordAddressPSMZ32(dbp, dbw, x, y);
                var word = addr < VramWords ? _vram[addr] : 0u;
                // PSMZ24 stays at 128 because depth-as-color overlaps unrelated colour
                // FBPs (see Z-to-VRAM memory note about rollback).
                var alpha = dpsm == PSMZ24 ? 128 : (int)(word >> 24);
                return ((byte)word, (byte)(word >> 8), (byte)(word >> 16), (byte)alpha);
            }
            case PSMCT16:
            case PSMCT16S:
            {
                var (wordAddr, half) = GetWordAddressPSMCT16(dbp, dbw, x, y, dpsm == PSMCT16S);
                return DecodePsmct16Pixel(wordAddr, half, texa);
            }
            case PSMZ16:
            case PSMZ16S:
            {
                var (wordAddr, half) = GetWordAddressPSMZ16(dbp, dbw, x, y, dpsm == PSMZ16S);
                return DecodePsmct16Pixel(wordAddr, half, texa);
            }
            default:
                return (0, 0, 0, 0);
        }
    }

    /// <summary>
    ///     PSMCT16/16S/PSMZ16/PSMZ16S framebuffer read. PS2 GS spec: the 1-bit alpha is
    ///     expanded via TEXA (TA1 when alpha-bit=1, TA0 otherwise) with AEM forcing 0 for
    ///     fully-black non-alpha pixels. The texa=0 fallback preserves the old "any value
    ///     will do for unrelated callers" contract; the GsGifInterpreter blend path always
    ///     supplies state.Texa so blends + write-confirmation readbacks see the same
    ///     spec-correct value.
    /// </summary>
    private (byte R, byte G, byte B, byte A) DecodePsmct16Pixel(int wordAddr, int half, ulong texa)
    {
        var word = wordAddr < VramWords ? _vram[wordAddr] : 0u;
        var pixel = half == 0 ? (ushort)(word & 0xFFFF) : (ushort)(word >> 16);
        byte alphaByte;
        if (texa != 0)
            alphaByte = Ps2TexPixelDecoder.ExpandTexaAlpha(pixel, texa);
        else
            alphaByte = (pixel & 0x8000) != 0 ? (byte)255 : (byte)0;
        return (Expand5(pixel & 0x1F), Expand5((pixel >> 5) & 0x1F), Expand5((pixel >> 10) & 0x1F), alphaByte);
    }
}
