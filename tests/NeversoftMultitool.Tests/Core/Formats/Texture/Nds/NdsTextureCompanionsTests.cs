using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Nds;

/// <summary>
///     Pins the route a browsing caller takes to a DS texture: it holds one container
///     entry and has to reach the sibling that carries the pixels.
///
///     The value of the test is that it arrives at the pinned corpus census by a
///     DIFFERENT road than the <c>nds-texture</c> command does. The command indexes
///     every texel blob in the container up front and hands the bank parser a lookup;
///     a tab has only an <see cref="AssetSource" /> per row and must ask for each
///     companion by name. If those two ever disagreed, one of them would be finding
///     banks the other cannot decode.
/// </summary>
public sealed class NdsTextureCompanionsTests(TestPaths paths)
{
    [Fact]
    public void TexelName_SpellsTheLoadersOwnTemplate()
    {
        Assert.Equal("0067ee06.texture.bin", NdsTextureCompanions.TexelName(0x0067EE06));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
        "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", 91, 1120)]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", 46, 1619)]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", 77, 1849)]
    public void RealCart_CompanionRouteFindsTheSameBanksTheIndexRouteDoes(
        string build, string rom, string gobPath, int expectedBanks, int expectedTextures)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        // Opened the way a tab opens it: a backend over the cart, then the nested GOB.
        var cart = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(cart);
        var gobEntry = cart!.FileSystem.FindByPath(gobPath);
        Assert.NotNull(gobEntry);
        var backend = cart.TryOpenNested(gobEntry!);
        Assert.NotNull(backend);

        var gob = backend!.FileSystem;
        var banks = 0;
        var textures = 0;
        var decoded = 0;
        foreach (var entry in gob.Entries)
        {
            // The tab admits a row by NAME, so the test does too. Every content-valid
            // bank in all three carts carries this name, which is what makes keying on
            // it lossless — see the note in TextureTabTextureOperations.
            if (!entry.Name.EndsWith(".textureinfo.bin", StringComparison.OrdinalIgnoreCase))
                continue;

            var source = new ArchiveAssetSource(backend, entry);
            byte[] data;
            try
            {
                data = source.ReadBytes();
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!NdsTextureCompanions.TryParseBank(source, data, out var bank, out var texels))
                continue;

            banks++;
            textures += bank.Count;
            foreach (var record in bank)
            {
                var pixels = texels(record.PixelId);
                Assert.NotNull(pixels);
                var rgba = NdsTextureDecoder.Decode(record, pixels!);
                Assert.Equal(record.Width * record.Height * 4, rgba.Length);
                decoded++;
            }
        }

        Assert.Equal(expectedBanks, banks);
        Assert.Equal(expectedTextures, textures);
        // Every record a bank declares decodes; none is left as a broken row.
        Assert.Equal(textures, decoded);
    }
}
