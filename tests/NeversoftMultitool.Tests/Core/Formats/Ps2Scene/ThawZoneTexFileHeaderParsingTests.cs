using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Tests.Core.Formats.Ps2Scene.ThawZoneTexFileTestHelper;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawZoneTexFileHeaderParsingTests
{
    [Fact]
    public void ParseVramUploads_UsesImagePayloadInsteadOfGifTagBytes()
    {
        var payload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var bytes = new List<byte>();

        WriteQword(bytes, 4 | (1ul << 60));
        WriteQword(bytes, 0x0E);

        var bitbltbuf = ((ulong)0x123 << 32) | (1ul << 48) | ((ulong)Ps2GsVram.PSMCT16 << 56);
        WriteQword(bytes, bitbltbuf);
        WriteQword(bytes, 0x50);

        WriteQword(bytes, 0);
        WriteQword(bytes, 0x51);

        var trxreg = 8ul | (2ul << 32);
        WriteQword(bytes, trxreg);
        WriteQword(bytes, 0x52);

        WriteQword(bytes, 0);
        WriteQword(bytes, 0x53);

        WriteQword(bytes, 2 | (2ul << 58));
        WriteQword(bytes, 0);
        bytes.AddRange(payload);

        var uploads = ThawZoneTexFile.ParseVramUploads(bytes.ToArray());

        var upload = Assert.Single(uploads);
        Assert.Equal((uint)0x123, upload.Dbp);
        Assert.Equal((uint)1, upload.Dbw);
        Assert.Equal(Ps2GsVram.PSMCT16, upload.Dpsm);
        Assert.Equal(8, upload.Width);
        Assert.Equal(2, upload.Height);
        Assert.Equal(payload, upload.PixelData);
    }

    [Fact]
    public void ParseHeaderEntries_ReadsZoneTexMetadataEntries()
    {
        var bytes = new byte[0x180];

        WriteUInt32(bytes, 0x40, 0x12345678);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, 128, 128, 0x359F,
            Ps2TexPixelDecoder.PSMCT16));
        WriteUInt32(bytes, 0x6C, 0x2A00);
        WriteUInt32(bytes, 0x70, 0x00000000);
        WriteUInt32(bytes, 0x74, 0x20);
        WriteUInt32(bytes, 0x78, 0xE0);

        WriteUInt32(bytes, 0x80, 0x23456789);
        WriteUInt64(bytes, 0x90, BuildTex0(0x2C80, 2, Ps2TexPixelDecoder.PSMT4, 64, 32, 0x359D,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0xAC, 0x400);
        WriteUInt32(bytes, 0xB0, 0x2A20);
        WriteUInt32(bytes, 0xB4, 0x40);
        WriteUInt32(bytes, 0xB8, 0x2E0);

        var gifOff = 0x100;
        WriteUInt64(bytes, gifOff, 4 | (1ul << 60));
        WriteUInt64(bytes, gifOff + 8, 0x0E);
        WriteUInt64(bytes, gifOff + 16, ((ulong)0x123 << 32) | (1ul << 48) | ((ulong)Ps2GsVram.PSMCT16 << 56));
        WriteUInt64(bytes, gifOff + 24, 0x50);
        WriteUInt64(bytes, gifOff + 32, 0);
        WriteUInt64(bytes, gifOff + 40, 0x51);
        WriteUInt64(bytes, gifOff + 48, 8ul | (2ul << 32));
        WriteUInt64(bytes, gifOff + 56, 0x52);
        WriteUInt64(bytes, gifOff + 64, 0);
        WriteUInt64(bytes, gifOff + 72, 0x53);
        WriteUInt64(bytes, gifOff + 80, 2 | (2ul << 58));
        WriteUInt64(bytes, gifOff + 88, 0);

        var entries = ThawZoneTexFile.ParseHeaderEntries(bytes);

        Assert.Equal(2, entries.Count);
        Assert.Equal((uint)0x12345678, entries[0].Checksum);
        Assert.Equal((uint)0x2A00, entries[0].DataSize);
        Assert.Equal((uint)0x20, entries[0].PaletteBytes);
        Assert.Equal((uint)0xE0, entries[0].UploadOffset);
        Assert.Equal((uint)0, entries[0].MipLevelCount);
        Assert.Equal((uint)0, entries[0].BasePixelBytes);
        Assert.Equal((uint)0, entries[0].LayoutMode);
        Assert.Equal((uint)0x23456789, entries[1].Checksum);
        Assert.Equal((uint)0x2A20, entries[1].DataOffset);
        Assert.Equal((uint)0x40, entries[1].PaletteBytes);
        Assert.Equal((uint)0x2E0, entries[1].UploadOffset);
        Assert.Equal((uint)0, entries[1].MipLevelCount);
        Assert.Equal((uint)0, entries[1].BasePixelBytes);
        Assert.Equal((uint)0, entries[1].LayoutMode);
    }

    [Fact]
    public void ParseHeaderEntries_PreservesMipCountAndBasePixelBytes()
    {
        const int firstGifOffset = 0x100;
        const uint checksum = 0x89ABCDEF;
        const int width = 16;
        const int height = 8;
        const uint basePixelBytes = width * height / 2;

        var bytes = new byte[firstGifOffset + 0x90];

        WriteUInt32(bytes, 0x40, checksum);
        WriteUInt32(bytes, 0x48, 2);
        WriteUInt32(bytes, 0x4C, 0x02000005);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0x6C, 0x2A00);
        WriteUInt32(bytes, 0x70, 0x20);
        WriteUInt32(bytes, 0x74, 0x20);
        WriteUInt32(bytes, 0x78, 0xE0);
        WriteUInt32(bytes, 0x7C, basePixelBytes << 12);

        WriteUInt64(bytes, firstGifOffset, 4 | (1ul << 60));
        WriteUInt64(bytes, firstGifOffset + 8, 0x0E);
        WriteUInt64(bytes, firstGifOffset + 16,
            ((ulong)0x2BC0 << 32) | (1ul << 48) | ((ulong)Ps2GsVram.PSMCT32 << 56));
        WriteUInt64(bytes, firstGifOffset + 24, 0x50);
        WriteUInt64(bytes, firstGifOffset + 32, 0);
        WriteUInt64(bytes, firstGifOffset + 40, 0x51);
        WriteUInt64(bytes, firstGifOffset + 48, 64ul | (32ul << 32));
        WriteUInt64(bytes, firstGifOffset + 56, 0x52);
        WriteUInt64(bytes, firstGifOffset + 64, 0);
        WriteUInt64(bytes, firstGifOffset + 72, 0x53);
        WriteUInt64(bytes, firstGifOffset + 80, 2 | (2ul << 58));
        WriteUInt64(bytes, firstGifOffset + 88, 0);

        var entry = Assert.Single(ThawZoneTexFile.ParseHeaderEntries(bytes));
        Assert.Equal((uint)2, entry.MipLevelCount);
        Assert.Equal(basePixelBytes, entry.BasePixelBytes);
        Assert.Equal((uint)0x02000005, entry.LayoutMode);
    }

    [Fact]
    public void TryResolveHeaderSourceEntry_PrefersExactTex0MatchWithinAliasGroup()
    {
        var alias128 = new ThawZoneTexFile.ZoneTexHeaderEntry(
            0x11111111,
            BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, 128, 128, 0x3590, Ps2TexPixelDecoder.PSMCT32),
            0x2000,
            0x100,
            0x20,
            0xE0);
        var alias64x128 = new ThawZoneTexFile.ZoneTexHeaderEntry(
            0x22222222,
            BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, 64, 128, 0x3590, Ps2TexPixelDecoder.PSMCT32),
            0x1000,
            0x2200,
            0x20,
            0x2E0);

        var exactMap = ThawZoneTexFile.BuildHeaderSourceEntryMapByTex0FromHeaderLists([[alias128, alias64x128]]);
        var groups = ThawZoneTexFile.BuildHeaderSourceEntryGroupsFromHeaderLists([[alias128, alias64x128]]);

        Assert.True(ThawZoneTexFile.TryResolveHeaderSourceEntry(
            alias64x128.Tex0, 0, exactMap, groups, out var resolved));
        Assert.Equal(alias64x128.Checksum, resolved.Entry.Checksum);
    }

    [Fact]
    public void TryResolveHeaderSourceEntry_PrefersMatchingMipCountWhenExactTex0IsMissing()
    {
        var baseTex0 = BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, 128, 128, 0x3590,
            Ps2TexPixelDecoder.PSMCT32);
        var requestedTex0 = baseTex0 | (2ul << 61);
        var baseOnly = new ThawZoneTexFile.ZoneTexHeaderEntry(
            0x33333333,
            baseTex0,
            0x2000,
            0x100,
            0x20,
            0xE0,
            0,
            0x2000);
        var mip2 = new ThawZoneTexFile.ZoneTexHeaderEntry(
            0x44444444,
            baseTex0 | (1ul << 61),
            0x2A00,
            0x2200,
            0x20,
            0x2E0,
            2,
            0x2000);

        var exactMap = ThawZoneTexFile.BuildHeaderSourceEntryMapByTex0FromHeaderLists([[baseOnly, mip2]]);
        var groups = ThawZoneTexFile.BuildHeaderSourceEntryGroupsFromHeaderLists([[baseOnly, mip2]]);
        var tex1 = 2ul << 2;

        Assert.True(ThawZoneTexFile.TryResolveHeaderSourceEntry(
            requestedTex0, tex1, exactMap, groups, out var resolved));
        Assert.Equal(mip2.Checksum, resolved.Entry.Checksum);
    }

    [Fact]
    public void BuildSourceIndexMapFromHeaderLists_MapsTexturesToOwningSource()
    {
        var headers = new[]
        {
            new[]
            {
                new ThawZoneTexFile.ZoneTexHeaderEntry(
                    0x11111111,
                    BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, 64, 64, 0x3590, Ps2TexPixelDecoder.PSMCT16),
                    0, 0, 0, 0)
            },
            new[]
            {
                new ThawZoneTexFile.ZoneTexHeaderEntry(
                    0x22222222,
                    BuildTex0(0x2CC0, 2, Ps2TexPixelDecoder.PSMT4, 64, 64, 0x35A0, Ps2TexPixelDecoder.PSMCT32),
                    0, 0, 0, 0)
            }
        };

        var sourceMap = ThawZoneTexFile.BuildSourceIndexMapFromHeaderLists(headers);

        Assert.Equal(0, sourceMap[(0x2BC0, 0x3590)]);
        Assert.Equal(1, sourceMap[(0x2CC0, 0x35A0)]);
    }
}
