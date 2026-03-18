using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Tests.Core.Formats.Ps2Scene.ThawZoneTexFileTestHelper;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawZoneTexFileHeaderDecodeTests
{
    [Fact]
    public void DecodeFromHeaderEntries_DecodesPackedSlotDataWithoutUploads()
    {
        const int firstGifOffset = 0x100;
        const int slotBaseOffset = 0x200;
        const int dataOffset = 0x20;
        const uint checksum = 0x89ABCDEF;
        const uint paletteBytes = 0x40;
        const int width = 128;
        const int height = 128;
        var pixelBytes = width * height / 2;

        var bytes = new byte[slotBaseOffset + dataOffset + paletteBytes + pixelBytes];

        WriteUInt32(bytes, 0x40, checksum);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0x6C, (uint)pixelBytes);
        WriteUInt32(bytes, 0x70, dataOffset);
        WriteUInt32(bytes, 0x74, paletteBytes);
        WriteUInt32(bytes, 0x78, 0xE0);

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

        var slotOffset = slotBaseOffset + dataOffset;
        var palette = BuildCt32Palette(255, 0, 0, 0x80);
        Array.Copy(palette, 0, bytes, slotOffset, palette.Length);

        var textures = ThawZoneTexFile.DecodeFromHeaderEntries(
            bytes,
            [],
            [
                new ThawZoneTexFile.ZoneTexHeaderEntry(
                    checksum,
                    BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
                        Ps2TexPixelDecoder.PSMCT32),
                    (uint)pixelBytes,
                    dataOffset,
                    paletteBytes,
                    0xE0)
            ]);

        var texture = Assert.Single(textures);
        Assert.NotNull(texture.Pixels);
        Assert.Equal((byte)255, texture.Pixels![0]);
        Assert.Equal((byte)0, texture.Pixels[1]);
        Assert.Equal((byte)0, texture.Pixels[2]);
        Assert.Equal((byte)255, texture.Pixels[3]);
    }

    [Fact]
    public void DecodeFromHeaderEntries_DecodesZeroOffsetPackedSlotDataWithoutFallingBackToUploads()
    {
        const int firstGifOffset = 0x100;
        const int slotBaseOffset = 0x200;
        const uint checksum = 0xA1B2C3D4;
        const uint paletteBytes = 0x40;
        const int width = 128;
        const int height = 128;
        var pixelBytes = width * height / 2;

        var bytes = new byte[slotBaseOffset + paletteBytes + pixelBytes];

        WriteUInt32(bytes, 0x40, checksum);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0x6C, (uint)pixelBytes);
        WriteUInt32(bytes, 0x70, 0);
        WriteUInt32(bytes, 0x74, paletteBytes);
        WriteUInt32(bytes, 0x78, 0xE0);

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

        var slotOffset = slotBaseOffset;
        var palette = BuildCt32Palette(0, 255, 0, 0x80);
        Array.Copy(palette, 0, bytes, slotOffset, palette.Length);

        var textures = ThawZoneTexFile.DecodeFromHeaderEntries(
            bytes,
            [],
            [
                new ThawZoneTexFile.ZoneTexHeaderEntry(
                    checksum,
                    BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
                        Ps2TexPixelDecoder.PSMCT32),
                    (uint)pixelBytes,
                    0,
                    paletteBytes,
                    0xE0)
            ]);

        var texture = Assert.Single(textures);
        AssertSolidColor(texture, 0, 255, 0, 255);
    }

    [Fact]
    public void DecodeFromHeaderEntries_PrefersMorePaletteLikeBiasedDataOffsetForNonZeroPackedSlots()
    {
        const int firstGifOffset = 0x100;
        const int slotBaseOffset = 0x200;
        const int dataOffset = 0x20;
        const uint checksum = 0xCAFEBABE;
        const uint paletteBytes = 0x40;
        const uint biasPaletteBytes = 0x80;
        const int width = 16;
        const int height = 8;
        var pixelBytes = width * height / 2;

        var bytes = new byte[slotBaseOffset + 0xA0 + paletteBytes + pixelBytes];

        WriteUInt32(bytes, 0x40, 0xA1B2C3D4);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0x6C, dataOffset + biasPaletteBytes);
        WriteUInt32(bytes, 0x70, 0);
        WriteUInt32(bytes, 0x74, biasPaletteBytes);
        WriteUInt32(bytes, 0x78, 0xE0);

        WriteUInt32(bytes, 0x80, checksum);
        WriteUInt64(bytes, 0x90, BuildTex0(0x2CC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x35A0,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0xAC, (uint)pixelBytes);
        WriteUInt32(bytes, 0xB0, dataOffset);
        WriteUInt32(bytes, 0xB4, paletteBytes);
        WriteUInt32(bytes, 0xB8, 0x2E0);

        WriteUInt32(bytes, 0xC0, 0x0BADF00D);
        WriteUInt64(bytes, 0xD0, BuildTex0(0x2DC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x35B0,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0xEC, (uint)pixelBytes);
        WriteUInt32(bytes, 0xF0, 0xA0);
        WriteUInt32(bytes, 0xF4, paletteBytes);
        WriteUInt32(bytes, 0xF8, 0x4E0);

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

        var unbiasedSlotOffset = slotBaseOffset + dataOffset;
        var unbiasedPalette = BuildCt32PaletteLowEntropy(255, 0, 0, 0x80);
        Array.Copy(unbiasedPalette, 0, bytes, unbiasedSlotOffset, unbiasedPalette.Length);

        var biasedSlotOffset = unbiasedSlotOffset + (int)biasPaletteBytes;
        var biasedPalette = BuildCt32PaletteWithEntropy(0, 0, 255, 0x80, 0xE3);
        Array.Copy(biasedPalette, 0, bytes, biasedSlotOffset, biasedPalette.Length);

        var entry = new ThawZoneTexFile.ZoneTexHeaderEntry(
            checksum,
            BuildTex0(0x2CC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x35A0,
                Ps2TexPixelDecoder.PSMCT32),
            (uint)pixelBytes,
            dataOffset,
            paletteBytes,
            0x2E0);

        Assert.True(ThawZoneTexFile.TryGetHeaderDataLayout(bytes, out var dataBaseOffset, out var dataOffsetBias));
        Assert.Equal((int)biasPaletteBytes,
            ThawZoneTexFile.SelectHeaderDataSlotBias(bytes, dataBaseOffset, dataOffsetBias, entry));
    }

    [Fact]
    public void DecodeFromHeaderEntries_PrefersMorePaletteLikeUnbiasedDataOffsetForNonZeroPackedSlots()
    {
        const int firstGifOffset = 0x100;
        const int slotBaseOffset = 0x200;
        const int dataOffset = 0x20;
        const uint checksum = 0xDEADC0DE;
        const uint paletteBytes = 0x40;
        const uint biasPaletteBytes = 0x80;
        const int width = 16;
        const int height = 8;
        var pixelBytes = width * height / 2;

        var bytes = new byte[slotBaseOffset + 0xA0 + paletteBytes + pixelBytes];

        WriteUInt32(bytes, 0x40, 0x10203040);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0x6C, dataOffset + biasPaletteBytes);
        WriteUInt32(bytes, 0x70, 0);
        WriteUInt32(bytes, 0x74, biasPaletteBytes);
        WriteUInt32(bytes, 0x78, 0xE0);

        WriteUInt32(bytes, 0x80, checksum);
        WriteUInt64(bytes, 0x90, BuildTex0(0x2CC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x35A0,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0xAC, (uint)pixelBytes);
        WriteUInt32(bytes, 0xB0, dataOffset);
        WriteUInt32(bytes, 0xB4, paletteBytes);
        WriteUInt32(bytes, 0xB8, 0x2E0);

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

        var unbiasedSlotOffset = slotBaseOffset + dataOffset;
        var unbiasedPalette = BuildCt32PaletteWithEntropy(0, 255, 0, 0x80, 0xD7);
        Array.Copy(unbiasedPalette, 0, bytes, unbiasedSlotOffset, unbiasedPalette.Length);

        var biasedSlotOffset = unbiasedSlotOffset + (int)biasPaletteBytes;
        var biasedPalette = BuildCt32PaletteLowEntropy(0, 0, 255, 0x80);
        Array.Copy(biasedPalette, 0, bytes, biasedSlotOffset, biasedPalette.Length);

        var entry = new ThawZoneTexFile.ZoneTexHeaderEntry(
            checksum,
            BuildTex0(0x2CC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x35A0,
                Ps2TexPixelDecoder.PSMCT32),
            (uint)pixelBytes,
            dataOffset,
            paletteBytes,
            0x2E0);

        Assert.True(ThawZoneTexFile.TryGetHeaderDataLayout(bytes, out var dataBaseOffset, out var dataOffsetBias));
        Assert.Equal(0, ThawZoneTexFile.SelectHeaderDataSlotBias(bytes, dataBaseOffset, dataOffsetBias, entry));
    }

    [Fact]
    public void DecodeFromHeaderEntries_ReordersPsmt4Ct32SlotClut()
    {
        const int firstGifOffset = 0x100;
        const int slotBaseOffset = 0x200;
        const int dataOffset = 0x20;
        const uint checksum = 0x76543210;
        const uint paletteBytes = 0x40;
        const int width = 128;
        const int height = 128;
        var pixelBytes = width * height / 2;

        var bytes = new byte[slotBaseOffset + dataOffset + paletteBytes + pixelBytes];

        WriteUInt32(bytes, 0x40, checksum);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0x6C, (uint)pixelBytes);
        WriteUInt32(bytes, 0x70, dataOffset);
        WriteUInt32(bytes, 0x74, paletteBytes);
        WriteUInt32(bytes, 0x78, 0xE0);

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

        var slotOffset = slotBaseOffset + dataOffset;
        var palette = BuildRawCt32Psmt4Palette(2, 255, 0, 0, 0x80, 8, 0, 0, 255, 0x80);
        Array.Copy(palette, 0, bytes, slotOffset, palette.Length);
        Array.Fill(bytes, (byte)0x88, slotOffset + palette.Length, pixelBytes);

        var textures = ThawZoneTexFile.DecodeFromHeaderEntries(
            bytes,
            [],
            [
                new ThawZoneTexFile.ZoneTexHeaderEntry(
                    checksum,
                    BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
                        Ps2TexPixelDecoder.PSMCT32),
                    (uint)pixelBytes,
                    dataOffset,
                    paletteBytes,
                    0xE0)
            ]);

        var texture = Assert.Single(textures);
        AssertSolidColor(texture, 255, 0, 0, 255);
    }

    [Fact]
    public void DecodeFromHeaderEntries_ReordersPsmt4Ct16SlotClut()
    {
        const int firstGifOffset = 0x100;
        const int slotBaseOffset = 0x200;
        const int dataOffset = 0x20;
        const uint checksum = 0x13572468;
        const uint paletteBytes = 0x20;
        const int width = 128;
        const int height = 128;
        var pixelBytes = width * height / 2;

        var bytes = new byte[slotBaseOffset + dataOffset + paletteBytes + pixelBytes];

        WriteUInt32(bytes, 0x40, checksum);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT16));
        WriteUInt32(bytes, 0x6C, (uint)pixelBytes);
        WriteUInt32(bytes, 0x70, dataOffset);
        WriteUInt32(bytes, 0x74, paletteBytes);
        WriteUInt32(bytes, 0x78, 0xE0);

        WriteUInt64(bytes, firstGifOffset, 4 | (1ul << 60));
        WriteUInt64(bytes, firstGifOffset + 8, 0x0E);
        WriteUInt64(bytes, firstGifOffset + 16,
            ((ulong)0x2BC0 << 32) | (1ul << 48) | ((ulong)Ps2GsVram.PSMCT16 << 56));
        WriteUInt64(bytes, firstGifOffset + 24, 0x50);
        WriteUInt64(bytes, firstGifOffset + 32, 0);
        WriteUInt64(bytes, firstGifOffset + 40, 0x51);
        WriteUInt64(bytes, firstGifOffset + 48, 64ul | (32ul << 32));
        WriteUInt64(bytes, firstGifOffset + 56, 0x52);
        WriteUInt64(bytes, firstGifOffset + 64, 0);
        WriteUInt64(bytes, firstGifOffset + 72, 0x53);
        WriteUInt64(bytes, firstGifOffset + 80, 2 | (2ul << 58));
        WriteUInt64(bytes, firstGifOffset + 88, 0);

        var slotOffset = slotBaseOffset + dataOffset;
        var palette = BuildRawCt16Psmt4Palette(2, 0x001F, 8, 0x7C00);
        Array.Copy(palette, 0, bytes, slotOffset, palette.Length);
        Array.Fill(bytes, (byte)0x88, slotOffset + palette.Length, pixelBytes);

        var textures = ThawZoneTexFile.DecodeFromHeaderEntries(
            bytes,
            [],
            [
                new ThawZoneTexFile.ZoneTexHeaderEntry(
                    checksum,
                    BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
                        Ps2TexPixelDecoder.PSMCT16),
                    (uint)pixelBytes,
                    dataOffset,
                    paletteBytes,
                    0xE0)
            ]);

        var texture = Assert.Single(textures);
        AssertSolidColor(texture, 255, 0, 0, 255);
    }

    [Fact]
    public void DecodeFromHeaderEntries_UsesHighestEntropySameCbpClutForSupportedPsmt4Layouts()
    {
        const int firstGifOffset = 0x100;
        const int slotBaseOffset = 0x200;
        const uint checksumA = 0x11112222;
        const uint checksumB = 0x33334444;
        const uint paletteBytes = 0x40;
        const int width = 16;
        const int height = 8;
        const int slotLength = 0x80;
        const int secondDataOffset = slotLength;
        var pixelBytes = width * height / 2;

        var bytes = new byte[slotBaseOffset + secondDataOffset + slotLength];

        WriteUInt32(bytes, 0x40, checksumA);
        WriteUInt32(bytes, 0x4C, 0x02000005);
        WriteUInt64(bytes, 0x50, BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0x6C, (uint)pixelBytes);
        WriteUInt32(bytes, 0x70, 0);
        WriteUInt32(bytes, 0x74, paletteBytes);
        WriteUInt32(bytes, 0x78, 0xE0);

        WriteUInt32(bytes, 0x80, checksumB);
        WriteUInt32(bytes, 0x8C, 0x02000005);
        WriteUInt64(bytes, 0x90, BuildTex0(0x2CC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
            Ps2TexPixelDecoder.PSMCT32));
        WriteUInt32(bytes, 0xAC, (uint)pixelBytes);
        WriteUInt32(bytes, 0xB0, secondDataOffset);
        WriteUInt32(bytes, 0xB4, paletteBytes);
        WriteUInt32(bytes, 0xB8, 0x2E0);

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

        var firstSlotOffset = slotBaseOffset;
        Array.Copy(BuildCt32PaletteLowEntropy(0, 255, 0, 0x80), 0, bytes, firstSlotOffset, (int)paletteBytes);

        var secondSlotOffset = slotBaseOffset + secondDataOffset;
        Array.Copy(BuildCt32PaletteWithEntropy(255, 0, 0, 0x80, 0xD3), 0, bytes, secondSlotOffset,
            (int)paletteBytes);

        var textures = ThawZoneTexFile.DecodeFromHeaderEntries(
            bytes,
            [],
            [
                new ThawZoneTexFile.ZoneTexHeaderEntry(
                    checksumA,
                    BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
                        Ps2TexPixelDecoder.PSMCT32),
                    (uint)pixelBytes,
                    0,
                    paletteBytes,
                    0xE0,
                    0,
                    0,
                    0x02000005),
                new ThawZoneTexFile.ZoneTexHeaderEntry(
                    checksumB,
                    BuildTex0(0x2CC0, 2, Ps2TexPixelDecoder.PSMT4, width, height, 0x3590,
                        Ps2TexPixelDecoder.PSMCT32),
                    (uint)pixelBytes,
                    secondDataOffset,
                    paletteBytes,
                    0x2E0,
                    0,
                    0,
                    0x02000005)
            ]);

        Assert.Equal(2, textures.Count);
        AssertSolidColor(textures.Single(texture => texture.Checksum == checksumA), 255, 0, 0, 255);
        AssertSolidColor(textures.Single(texture => texture.Checksum == checksumB), 255, 0, 0, 255);
    }
}
