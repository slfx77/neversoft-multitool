using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Tests.Core.Formats.Ps2Scene.ThawZoneTexFileTestHelper;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawZoneTexFileClutTests
{
    [Fact]
    public void ReadClutPsmt4Csm1_Ct16_UsesPcsx2TableOrder()
    {
        var palette = new byte[32 * 2];
        for (var i = 0; i < 32; i++)
        {
            var value = (ushort)(0x1000 + i);
            palette[i * 2] = (byte)value;
            palette[i * 2 + 1] = (byte)(value >> 8);
        }

        var vram = new Ps2GsVram();
        vram.WriteRectPSMCT16(0, 1, 16, 2, palette);

        var clut = ThawZoneTexFile.ReadClutPsmt4Csm1(vram, 0, Ps2TexPixelDecoder.PSMCT16);

        Assert.NotNull(clut);
        var entries = Enumerable.Range(0, 16)
            .Select(i => BitConverter.ToUInt16(clut!, i * 2))
            .ToArray();

        Assert.Equal(
            Enumerable.Range(0, 8)
                .Select(i => (ushort)(0x1000 + i))
                .Concat(Enumerable.Range(0, 8).Select(i => (ushort)(0x1010 + i)))
                .ToArray(),
            entries);
    }

    [Fact]
    public void ReadClutPsmt4Csm1_Ct32_UsesPcsx2TableOrder()
    {
        var palette = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            var baseOff = i * 4;
            palette[baseOff] = (byte)i;
            palette[baseOff + 1] = (byte)(i + 0x10);
            palette[baseOff + 2] = (byte)(i + 0x20);
            palette[baseOff + 3] = (byte)(i + 0x30);
        }

        var vram = new Ps2GsVram();
        vram.WriteRectPSMCT32(0, 1, 8, 2, palette);

        var clut = ThawZoneTexFile.ReadClutPsmt4Csm1(vram, 0, Ps2TexPixelDecoder.PSMCT32);
        var rawWords = vram.ReadRawBlockWords(0, 16);

        Assert.NotNull(clut);
        var entries = Enumerable.Range(0, 16)
            .Select(i => BitConverter.ToUInt32(clut!, i * 4))
            .ToArray();

        Assert.Equal(
            new[]
            {
                rawWords[0], rawWords[1], rawWords[4], rawWords[5],
                rawWords[8], rawWords[9], rawWords[12], rawWords[13],
                rawWords[2], rawWords[3], rawWords[6], rawWords[7],
                rawWords[10], rawWords[11], rawWords[14], rawWords[15]
            },
            entries);
    }
}
