using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Texture.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Gba;

/// <summary>
///     Pins the THPS2 GBA sprite-art extraction: the self-validating deck /
///     character / venue tables and their proven decodes (each family was validated
///     byte-for-byte against live OBJ VRAM/OAM/palette captures during the RE).
/// </summary>
public sealed class GbaSpriteArtTests(TestPaths paths)
{
    private byte[]? LoadThps2()
    {
        var path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");
        return path == null ? null : File.ReadAllBytes(path);
    }

    private static string Sha(byte[] rgba) => Convert.ToHexStringLower(SHA256.HashData(rgba));

    [CorpusFact]
    public void ExtractsTheProvenSpriteArtFamilies()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");

        // The shared select palette resolves by content (the 512-byte full palette
        // whose first 200 bytes equal the short select palette stream).
        Assert.NotNull(GbaSpriteArt.FindSelectPalette(rom));

        // 123 decks — each paired with its OWN trailing palette (the aligned-end
        // equality is the validator; the table-adjacent palette belongs to the NEXT
        // deck and merely looks plausible). Deck 0's RGBA is pinned.
        var decks = GbaSpriteArt.ExtractDecks(rom);
        Assert.Equal(123, decks.Count);
        Assert.Equal(
            "50e879ca3f487f3083f1ef6c146f0c31aa2af049b9e09493e71cb6f51349a38d",
            Sha(decks[0].Rgba));

        // 15 roster portraits; record 13 is Spider-Man.
        var portraits = GbaSpriteArt.ExtractPortraits(rom);
        Assert.Equal(15, portraits.Count);
        Assert.Equal(
            "556571ee26c51c989350486c1ea0e91e4e42566e7132b5bafd185b40586b54b4",
            Sha(portraits[13].Rgba));

        // 14 distinct venue photographs across the level records' +0x44/+0x48 slots.
        var venues = GbaSpriteArt.ExtractVenuePhotos(rom);
        Assert.Equal(14, venues.Count);
        Assert.Equal(
            "b92b48486052d7c8ce45ddd65b8a76f1c941bd1d0fd310bb69adde315313077f",
            Sha(venues[0].Rgba));
    }
}
