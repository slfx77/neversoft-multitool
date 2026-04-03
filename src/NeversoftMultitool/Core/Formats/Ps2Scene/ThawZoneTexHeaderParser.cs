using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawZoneTexHeaderParser
{
    private const int HeaderEntrySize = 0x40;

    public static List<ThawZoneTexFile.ZoneTexHeaderEntry> ParseHeaderEntries(ReadOnlySpan<byte> data)
    {
        var firstGif = ThawZoneTexFile.FindFirstGifAdBlock(data);
        if (firstGif < HeaderEntrySize)
            return [];

        var entries = new List<ThawZoneTexFile.ZoneTexHeaderEntry>();
        var entryBase = 0x40;
        while (entryBase + HeaderEntrySize <= firstGif)
        {
            var tex0 = BitConverter.ToUInt64(data[(entryBase + 0x10)..]);
            var tbp = (uint)(tex0 & 0x3FFF);
            var tbw = (uint)((tex0 >> 14) & 0x3F);
            var psm = (uint)((tex0 >> 20) & 0x3F);
            var tw = (int)((tex0 >> 26) & 0xF);
            var th = (int)((tex0 >> 30) & 0xF);
            var cbp = (uint)((tex0 >> 37) & 0x3FFF);
            var cpsm = (uint)((tex0 >> 51) & 0xF);

            if (!Ps2TexPixelDecoder.IsValidPsm(psm) ||
                tw < 1 || tw > 10 ||
                th < 1 || th > 10 ||
                tbp < 0x2BC0 ||
                tbw < 1 ||
                cbp < 0x3000 ||
                cpsm is not (Ps2TexPixelDecoder.PSMCT16 or Ps2TexPixelDecoder.PSMCT32))
            {
                continue;
            }

            var checksum = BitConverter.ToUInt32(data[entryBase..]);
            if (checksum <= 0xFFFF)
            {
                entryBase += 8;
                continue;
            }

            entries.Add(new ThawZoneTexFile.ZoneTexHeaderEntry(
                checksum,
                tex0,
                BitConverter.ToUInt32(data[(entryBase + 0x2C)..]),
                BitConverter.ToUInt32(data[(entryBase + 0x30)..]),
                BitConverter.ToUInt32(data[(entryBase + 0x34)..]),
                BitConverter.ToUInt32(data[(entryBase + 0x38)..]),
                BitConverter.ToUInt32(data[(entryBase + 0x08)..]),
                BitConverter.ToUInt32(data[(entryBase + 0x3C)..]) >> 12,
                BitConverter.ToUInt32(data[(entryBase + 0x0C)..])));

            entryBase += HeaderEntrySize;
        }

        return entries;
    }
}
