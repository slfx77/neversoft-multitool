using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Nds;

/// <summary>
///     Pins the Vicarious Visions DS texture bank and the GX texture decode.
///
///     The bank has no magic, so its detection rests on an identity every record
///     must satisfy: the width and height encoded in the record's own
///     TEXIMAGE_PARAM bits, times the format's bits-per-texel, must equal the
///     declared texel-byte count. The corpus test additionally requires every
///     record's pixel id to resolve to a real GOB file of exactly that length —
///     two independent statements that a mis-read layout cannot satisfy together.
/// </summary>
public sealed class NdsTextureBankTests(TestPaths paths)
{
    private static readonly (string Build, string Rom, string Gob, int Banks, int Textures)[] Carts =
    [
        ("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", 91, 1120),
        ("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
            "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", 46, 1619),
        ("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
            "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", 77, 1849)
    ];

    [Fact]
    public void TryParse_ReadsRecordsAndPalette()
    {
        var bank = BuildBank(width: 32, height: 16, format: NdsTextureFormat.Palette16, paletteEntries: 16);
        Assert.True(NdsTextureBank.TryParse(bank, out var textures));
        var texture = Assert.Single(textures!);

        Assert.Equal(0xDEADBEEFu, texture.PixelId);
        Assert.Equal(NdsTextureFormat.Palette16, texture.Format);
        Assert.Equal(32, texture.Width);
        Assert.Equal(16, texture.Height);
        Assert.Equal(32 * 16 / 2, texture.PixelBytes);
        Assert.Equal(16, texture.Palette.Length);
        Assert.Equal(".\\deadbeef.texture.bin", texture.PixelFileName);
    }

    [Fact]
    public void TryParse_RejectsARecordWhoseSizeIdentityFails()
    {
        var bank = BuildBank(32, 16, NdsTextureFormat.Palette16, 16);
        // Declared texel bytes no longer match width*height*bpp/8.
        BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(8 + 4), 999);
        Assert.False(NdsTextureBank.TryParse(bank, out _));
    }

    [Fact]
    public void TryParse_RejectsAnUnknownFormat()
    {
        var bank = BuildBank(32, 16, NdsTextureFormat.Palette16, 16);
        var param = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(8 + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(8 + 8), param & ~(7u << 26)); // format 0
        Assert.False(NdsTextureBank.TryParse(bank, out _));
    }

    [Fact]
    public void Decode_Palette16UsesLowNibbleFirst()
    {
        // Low-nibble-first is the DS layout, and it is what the corpus measures:
        // scored over 205 real 16-colour textures it produces the horizontally
        // continuous image 204 times to 1.
        // DS texture sizes are 8 << n, so 8x8 is the smallest legal texture.
        var bank = BuildBank(8, 8, NdsTextureFormat.Palette16, 16);
        Assert.True(NdsTextureBank.TryParse(bank, out var textures));
        var entry = textures![0];

        var texels = new byte[8 * 8 / 2];
        texels[0] = 0x21;
        texels[1] = 0x43;
        var rgba = NdsTextureDecoder.Decode(entry, texels);

        // Rows are stored bottom-up, so source row 0 lands on the LAST output row.
        const int lastRow = 7 * 8;
        Assert.Equal(entry.Palette[1], Pack(rgba, lastRow + 0));
        Assert.Equal(entry.Palette[2], Pack(rgba, lastRow + 1));
        Assert.Equal(entry.Palette[3], Pack(rgba, lastRow + 2));
        Assert.Equal(entry.Palette[4], Pack(rgba, lastRow + 3));
    }

    [Fact]
    public void Decode_HonoursColourZeroTransparency()
    {
        var texels = new byte[8 * 8 / 2];
        texels[0] = 0x10; // texel 0 = index 0, texel 1 = index 1

        var opaque = BuildBank(8, 8, NdsTextureFormat.Palette16, 16);
        Assert.True(NdsTextureBank.TryParse(opaque, out var noAlpha));
        Assert.Equal(255, NdsTextureDecoder.Decode(noAlpha![0], texels)[7 * 8 * 4 + 3]);

        var keyed = BuildBank(8, 8, NdsTextureFormat.Palette16, 16, colour0Transparent: true);
        Assert.True(NdsTextureBank.TryParse(keyed, out var keyedTextures));
        var rgba = NdsTextureDecoder.Decode(keyedTextures![0], texels);
        const int lastRow = 7 * 8 * 4;
        Assert.Equal(0, rgba[lastRow + 3]);   // index 0 is a hole
        Assert.Equal(255, rgba[lastRow + 7]); // index 1 is not
    }

    [Fact]
    public void Decode_StoresRowsBottomUp()
    {
        // Confirmed visually against textures with unambiguous subjects: decoding
        // in storage order renders the Jeep logo upside down and mirrors the
        // "SKATE SHOP" sign. A statistical coherence check cannot see this.
        var bank = BuildBank(8, 8, NdsTextureFormat.Palette16, 16);
        Assert.True(NdsTextureBank.TryParse(bank, out var textures));

        var texels = new byte[8 * 8 / 2];
        texels[0] = 0x11; // first STORED row uses palette index 1
        texels[1] = 0x11;
        texels[2] = 0x11;
        texels[3] = 0x11;
        var rgba = NdsTextureDecoder.Decode(textures![0], texels);

        Assert.Equal(textures[0].Palette[0], Pack(rgba, 0));          // top output row is empty
        Assert.Equal(textures[0].Palette[1], Pack(rgba, 7 * 8));      // bottom output row is the first stored row
    }

    [Fact]
    public void Decode_RejectsFourByFourCompressed()
    {
        var bank = BuildBank(8, 8, NdsTextureFormat.Compressed4X4, 4);
        Assert.True(NdsTextureBank.TryParse(bank, out var textures));
        var ex = Assert.Throws<InvalidDataException>(
            () => NdsTextureDecoder.Decode(textures![0], new byte[16]));
        Assert.Contains("second palette-index block", ex.Message, StringComparison.Ordinal);
    }

    [CorpusTheory]
    [MemberData(nameof(CartCases))]
    public void RealCart_EveryBankRecordResolvesAndDecodes(
        string build, string rom, string gobPath, int expectedBanks, int expectedTextures)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        Assert.NotNull(gob);

        var byPixelId = gob!.Entries
            .Where(e => e.Name.EndsWith(".texture.bin", StringComparison.Ordinal))
            .ToDictionary(e => uint.Parse(e.Name[..8], System.Globalization.NumberStyles.HexNumber));

        long? PixelLength(uint id) => byPixelId.TryGetValue(id, out var e) ? e.Size : null;

        var banks = 0;
        var textures = 0;
        var decoded = 0;
        foreach (var entry in gob.Entries)
        {
            byte[] data;
            try
            {
                data = gob.ReadEntry(entry);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!NdsTextureBank.TryParseValidated(data, PixelLength, out var parsed))
                continue;

            banks++;
            foreach (var texture in parsed!)
            {
                textures++;
                var pixelEntry = byPixelId[texture.PixelId];
                Assert.Equal(texture.PixelBytes, pixelEntry.Size);

                if (texture.Format == NdsTextureFormat.Compressed4X4)
                    continue;
                var rgba = NdsTextureDecoder.Decode(texture, gob.ReadEntry(pixelEntry));
                Assert.Equal(texture.Width * texture.Height * 4, rgba.Length);
                decoded++;
            }
        }

        Assert.Equal(expectedBanks, banks);
        Assert.Equal(expectedTextures, textures);
        Assert.Equal(textures, decoded);
    }

    public static TheoryData<string, string, string, int, int> CartCases()
    {
        var data = new TheoryData<string, string, string, int, int>();
        foreach (var (build, rom, gob, banks, textures) in Carts)
            data.Add(build, rom, gob, banks, textures);
        return data;
    }

    private static ushort Pack(byte[] rgba, int index)
    {
        // Re-encode a decoded pixel back to BGR555 so it can be compared to the palette.
        var r = rgba[index * 4] >> 3;
        var g = rgba[index * 4 + 1] >> 3;
        var b = rgba[index * 4 + 2] >> 3;
        return (ushort)(r | (g << 5) | (b << 10));
    }

    /// <summary>One-texture bank: header + a 28-byte record + a 16-byte palette record + palette.</summary>
    private static byte[] BuildBank(
        int width, int height, NdsTextureFormat format, int paletteEntries, bool colour0Transparent = false)
    {
        var bpp = NdsTextureBank.BitsPerTexel(format);
        var pixelBytes = width * height * bpp / 8;
        var bank = new byte[8 + 28 + 16 + 4 + paletteEntries * 2];

        BinaryPrimitives.WriteUInt16LittleEndian(bank, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bank.AsSpan(2), 1);

        var param = ((uint)format << 26)
                    | ((uint)ShiftFor(width) << 20)
                    | ((uint)ShiftFor(height) << 23)
                    | (colour0Transparent ? 1u << 29 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(8), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(12), (uint)pixelBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(16), param);

        BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(36), (uint)format);
        BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(40), 0); // offset, in u16 ENTRIES

        // Palette data is a self-describing {u32 count, u16 entries[count]} blob.
        BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(52), (uint)paletteEntries);
        for (var i = 0; i < paletteEntries; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bank.AsSpan(56 + i * 2), (ushort)(i * 0x0421));
        return bank;
    }

    private static int ShiftFor(int size)
    {
        var shift = 0;
        while (8 << shift < size)
            shift++;
        return shift;
    }
}
