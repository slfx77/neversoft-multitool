using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Nds;
using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Nds;

/// <summary>
///     Pins the binding a model and its texture bank get from the loader's own
///     naming: they belong to one model set and share its first id.
///
///     The property that matters is not just that the stated binding resolves more
///     models than the GX-state join — it is that the two NEVER DISAGREE. The join
///     only speaks when exactly one bank in the container is compatible, which makes
///     it a slow but independent referee; if spelling and structure ever picked
///     different banks, one of them would be wrong.
/// </summary>
public sealed class NdsModelSetTests(TestPaths paths)
{
    private static readonly (string Build, string Rom, string Gob)[] Carts =
    [
        ("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob"),
        ("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
            "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob"),
        ("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
            "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob")
    ];

    [Fact]
    public void ParsesTheTwoIdsOutOfAComposedGeometryName()
    {
        Assert.True(NdsModelSet.TryParseGeometryName(
            ".\\fcb00a34.532e1440.geometry.bin", out var a, out var b));
        Assert.Equal(0xFCB00A34u, a);
        Assert.Equal(0x532E1440u, b);
    }

    [Fact]
    public void DeclinesAnythingThatIsNotATwoIdGeometryName()
    {
        // An unnamed file, an indexed animation clip, a one-id name, and junk.
        Assert.False(NdsModelSet.TryParseGeometryName("3f2a10c8.bin", out _, out _));
        Assert.False(NdsModelSet.TryParseGeometryName(
            ".\\fcb00a34.532e1440.7.animation.bin", out _, out _));
        Assert.False(NdsModelSet.TryParseGeometryName(
            ".\\fcb00a34.textureinfo.bin", out _, out _));
        Assert.False(NdsModelSet.TryParseGeometryName(null, out _, out _));
        Assert.False(NdsModelSet.TryParseGeometryName(
            ".\\zzzzzzzz.532e1440.geometry.bin", out _, out _));
    }

    [Fact]
    public void TextureBankNameIsTheModelSetIdUnderTheLoadersOwnTemplate()
    {
        Assert.Equal(".\\fcb00a34.textureinfo.bin", NdsModelSet.TextureBankName(0xFCB00A34));
        Assert.Equal(GobNames.Hash(".\\fcb00a34.textureinfo.bin"),
            NdsModelSet.TextureBankKey(0xFCB00A34));
    }

    [CorpusTheory]
    [MemberData(nameof(BindingCases))]
    public void RealCart_StatedBindingResolvesMoreAndContradictsTheJoinNowhere(
        string build, string rom, string gobPath, string expected)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        Assert.NotNull(gob);

        var texels = new Dictionary<uint, long>();
        foreach (var entry in gob!.Entries)
        {
            if (entry.Name.EndsWith(".texture.bin", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(entry.Name.AsSpan(0, Math.Min(8, entry.Name.Length)),
                    System.Globalization.NumberStyles.HexNumber, null, out var id))
            {
                texels[id] = entry.Size;
            }
        }

        long? Length(uint id) => texels.TryGetValue(id, out var size) ? size : null;

        var banks = new List<IReadOnlyList<NdsTextureEntry>>();
        var banksByKey = new Dictionary<uint, IReadOnlyList<NdsTextureEntry>>();
        var geometry = new List<(uint Crc, byte[] Data)>();
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

            if (NdsTextureBank.TryParseValidated(data, Length, out var bank))
            {
                banks.Add(bank);
                banksByKey[entry.Crc] = bank;
            }
            else if (NdsGeometryFile.IsGeometry(data))
            {
                geometry.Add((entry.Crc, data));
            }
        }

        var textured = 0;
        var stated = 0;
        var joined = 0;
        var disagreed = 0;

        foreach (var (crc, data) in geometry)
        {
            if (!NdsGeometryFile.TryParseValidated(data, out var file))
                continue;
            var groups = NdsGxInterpreter.Run(data, file);
            if (!groups.Any(g => g.Indices.Count > 0 && g.Material.HasTexture
                                                     && g.Material.TextureIndex >= 0))
            {
                continue;
            }

            textured++;

            IReadOnlyList<NdsTextureEntry>? byName = null;
            if (NdsModelSet.TryParseGeometryName(GobNames.TryResolve(crc), out var idA, out _))
                banksByKey.TryGetValue(NdsModelSet.TextureBankKey(idA), out byName);

            var byJoin = NdsTextureBankResolver.Resolve(groups, banks);

            if (byName != null)
                stated++;
            if (byJoin != null)
                joined++;
            if (byName != null && byJoin != null && !ReferenceEquals(byName, byJoin))
                disagreed++;
        }

        Assert.Equal(0, disagreed);
        Assert.Equal(expected, $"{stated}/{joined}/{textured}");
    }

    public static TheoryData<string, string, string, string> BindingCases()
    {
        var data = new TheoryData<string, string, string, string>();
        data.Add(Carts[0].Build, Carts[0].Rom, Carts[0].Gob, "862/461/866");
        data.Add(Carts[1].Build, Carts[1].Rom, Carts[1].Gob, "944/280/946");
        data.Add(Carts[2].Build, Carts[2].Rom, Carts[2].Gob, "1329/324/1330");
        return data;
    }
}
