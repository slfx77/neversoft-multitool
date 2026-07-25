using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene;

public sealed class ZoneTextureProviderBuilderTests
{
    [Fact]
    public void GetTexFiles_ForWorldzonePakIncludesOnlyExactStemSiblings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_ZoneTex_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var names = new[]
            {
                "z_bh.pak.ps2",
                "z_bh_net.pak.ps2",
                "z_bh_accessoryshop_data.pak.ps2",
                "z_bhho.pak.ps2",
                "z_bhped.pak.ps2",
                "z_bhsm.pak.ps2",
                "z_bhsr_net.pak.ps2"
            };

            foreach (var name in names)
                File.WriteAllBytes(Path.Combine(tempDir, name), []);

            var files = ZoneTextureProviderBuilder.GetTexFiles(Path.Combine(tempDir, "z_bh.pak.ps2"))
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains("z_bh.pak.ps2", files);
            Assert.Contains("z_bh_net.pak.ps2", files);
            Assert.Contains("z_bh_accessoryshop_data.pak.ps2", files);
            Assert.DoesNotContain("z_bhho.pak.ps2", files);
            Assert.DoesNotContain("z_bhped.pak.ps2", files);
            Assert.DoesNotContain("z_bhsm.pak.ps2", files);
            Assert.DoesNotContain("z_bhsr_net.pak.ps2", files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MakeTex0IdentityKey_DiffersByPaletteAndBufferWidthBits()
    {
        var tex0 = MakeTex0(tbw: 4, csm: 0, csa: 1);
        var key = ZoneTextureProviderBuilder.MakeTex0IdentityKey(tex0);

        Assert.NotEqual(key, ZoneTextureProviderBuilder.MakeTex0IdentityKey(MakeTex0(tbw: 5, csm: 0, csa: 1)));
        Assert.NotEqual(key, ZoneTextureProviderBuilder.MakeTex0IdentityKey(MakeTex0(tbw: 4, csm: 1, csa: 1)));
        Assert.NotEqual(key, ZoneTextureProviderBuilder.MakeTex0IdentityKey(MakeTex0(tbw: 4, csm: 0, csa: 2)));
    }

    [Fact]
    public void MakeTex0IdentityKey_IgnoresRenderOnlyBits()
    {
        var tex0 = MakeTex0(tcc: 0, tfx: 0, cld: 0);
        var renderVariant = MakeTex0(tcc: 1, tfx: 3, cld: 7);

        Assert.Equal(
            ZoneTextureProviderBuilder.MakeTex0IdentityKey(tex0),
            ZoneTextureProviderBuilder.MakeTex0IdentityKey(renderVariant));
    }

    [Fact]
    public void ResolveTex0_WhenSameAddressAppearsInMultipleSourcesPrefersCurrentSource()
    {
        var tex0 = MakeTex0(0x100, cbp: 0x200);
        var catalog = ZoneTextureCatalog.CreateForTests("z_bh.pak.ps2",
        [
            ("z_bh.pak.ps2", MakeEntry(0x11111111, tex0), MakeTexture(0x11111111)),
            ("z_bh_net.pak.ps2", MakeEntry(0x22222222, tex0), MakeTexture(0x22222222))
        ]);

        var resolution = catalog.ResolveTex0(tex0, "z_bh_net.pak.ps2");

        Assert.Equal(0x22222222u, resolution.Checksum);
        Assert.Equal("same_source_exact", resolution.ResolveMode);
        Assert.Equal("z_bh_net.pak.ps2", resolution.SourceLabel);
    }

    [Fact]
    public void ResolveTex0_WhenCrossSourceTbpCbpIsAmbiguousReturnsUnresolved()
    {
        var first = MakeTex0(0x100, 4, cbp: 0x200, csa: 1);
        var second = MakeTex0(0x100, 4, cbp: 0x200, csa: 2);
        var request = MakeTex0(0x100, 5, cbp: 0x200, csa: 3);
        var catalog = ZoneTextureCatalog.CreateForTests("z_bh.pak.ps2",
        [
            ("z_bh.pak.ps2", MakeEntry(0x33333333, MakeTex0(0x999, cbp: 0x888)), MakeTexture(0x33333333)),
            ("z_bh_net.pak.ps2", MakeEntry(0x11111111, first), MakeTexture(0x11111111)),
            ("z_bh_accessoryshop_data.pak.ps2", MakeEntry(0x22222222, second), MakeTexture(0x22222222))
        ]);

        var resolution = catalog.ResolveTex0(request, "z_bh_unknown.pak.ps2");

        Assert.Equal(0u, resolution.Checksum);
        Assert.Equal("unresolved", resolution.ResolveMode);
    }

    [Fact]
    public void ResolveTex0_WhenSameSourceExactIsAmbiguousUsesMaterialGroupIndex()
    {
        var tex0 = MakeTex0(0x222, cbp: 0x333);
        var catalog = ZoneTextureCatalog.CreateForTests("z_bh.pak.ps2",
        [
            ("z_bh.pak.ps2", MakeEntry(0x11111111, tex0, 0xAAAA0001), MakeTexture(0x11111111), 9u),
            ("z_bh.pak.ps2", MakeEntry(0x22222222, tex0, 0xBBBB0002), MakeTexture(0x22222222), 11u)
        ]);

        var resolution = catalog.ResolveTex0(tex0, "z_bh.pak.ps2", 9);

        Assert.Equal(0x11111111u, resolution.Checksum);
        Assert.Equal("same_source_material_group_exact", resolution.ResolveMode);
    }

    [Fact]
    public void ResolveTex0_WhenSameSourceExactIsAmbiguousUsesTextureGroupChecksum()
    {
        var tex0 = MakeTex0(0x224, cbp: 0x335);
        var catalog = ZoneTextureCatalog.CreateForTests("z_bh.pak.ps2",
        [
            ("z_bh.pak.ps2", MakeEntry(0x33333333, tex0, 0xCAFE0001), MakeTexture(0x33333333)),
            ("z_bh.pak.ps2", MakeEntry(0x44444444, tex0, 0xCAFE0002), MakeTexture(0x44444444))
        ]);

        var resolution = catalog.ResolveTex0(tex0, "z_bh.pak.ps2", 0xCAFE0002);

        Assert.Equal(0x44444444u, resolution.Checksum);
        Assert.Equal("same_source_group_exact", resolution.ResolveMode);
    }

    [Fact]
    public void ResolveTex0_WhenSameSourceExactIsAmbiguousUsesEntryHint()
    {
        var tex0 = MakeTex0(0x226, cbp: 0x337);
        var catalog = ZoneTextureCatalog.CreateForTests("z_bh.pak.ps2",
        [
            ("z_bh.pak.ps2", "z_bh.pak.ps2::000022F0", MakeEntry(0x55555555, tex0), MakeTexture(0x55555555)),
            ("z_bh.pak.ps2", "z_bh.pak.ps2::00015D40", MakeEntry(0x66666666, tex0), MakeTexture(0x66666666))
        ]);

        var resolution = catalog.ResolveTex0(tex0, "z_bh.pak.ps2::00015D40");

        Assert.Equal(0x66666666u, resolution.Checksum);
        Assert.Equal("entry_exact", resolution.ResolveMode);
        Assert.Equal("z_bh.pak.ps2::00015D40", resolution.EntryLabel);
    }

    private static ulong MakeTex0(
        uint tbp = 0x123,
        uint tbw = 4,
        uint psm = 0x14,
        uint tw = 6,
        uint th = 6,
        uint tcc = 0,
        uint tfx = 0,
        uint cbp = 0x456,
        uint cpsm = 0,
        uint csm = 0,
        uint csa = 1,
        uint cld = 0)
    {
        return (tbp & 0x3FFFUL)
               | ((tbw & 0x3FUL) << 14)
               | ((psm & 0x3FUL) << 20)
               | ((tw & 0xFUL) << 26)
               | ((th & 0xFUL) << 30)
               | ((tcc & 0x1UL) << 34)
               | ((tfx & 0x3UL) << 35)
               | ((cbp & 0x3FFFUL) << 37)
               | ((cpsm & 0xFUL) << 51)
               | ((csm & 0x1UL) << 55)
               | ((csa & 0x1FUL) << 56)
               | ((cld & 0x7UL) << 61);
    }

    private static ThawZoneTexFile.ZoneTexHeaderEntry MakeEntry(
        uint checksum,
        ulong tex0,
        uint groupChecksum = 0)
    {
        return new ThawZoneTexFile.ZoneTexHeaderEntry(checksum, tex0, 0, 0, 0, 0, GroupChecksum: groupChecksum);
    }

    private static Ps2Texture MakeTexture(uint checksum)
    {
        return new Ps2Texture(checksum, 1, 1, 0, 0, [255, 255, 255, 255]);
    }
}