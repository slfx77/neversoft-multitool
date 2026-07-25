using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

/// <summary>
///     Regression for owner blobs whose DMA chain starts with a RET segment before
///     the first CNT tag. The archive-backed path must use the owner-table counts rather
///     than interpreting the RET tag and payload as a texture record.
/// </summary>
public sealed class ThawWorldzoneArchiveOwnerPreludeTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";
    private const string PakPath = "worlds/worldzones/z_sz/z_sz.pak.ps2";

    private string WadPath => paths.SampleBuildsDir is null
        ? string.Empty
        : Path.Combine(paths.SampleBuildsDir, ThawPs2Build, "DATAP.WAD");

    [Fact]
    public void ZszOwnerBlob_WithRetPrelude_DecodesAndBindsAllAuthoredRecords()
    {
        Assert.SkipWhen(!File.Exists(WadPath), "THAW PS2 DATAP.WAD sample not available");

        using var wad = ArchiveFileSystem.TryOpen(WadPath);
        Assert.SkipWhen(wad == null, "DATAP.WAD did not open as a WAD archive");

        var pakEntry = wad!.FindByPath(PakPath);
        Assert.NotNull(pakEntry);
        var pakBytes = wad.ReadEntry(pakEntry!);

        using var pak = wad.TryOpenNested(pakEntry!);
        Assert.NotNull(pak);
        var stexEntry = pak!.FindByName("000018A0.stex");
        var mdlEntry = pak.FindByName("00009630.mdl");
        Assert.NotNull(stexEntry);
        Assert.NotNull(mdlEntry);

        var stexBytes = pak.ReadEntry(stexEntry!);
        var mdlBytes = pak.ReadEntry(mdlEntry!);

        Assert.True(ThawZoneTexOwnerBlobDecoder.TryFindOwnerBlobHeader(
            stexBytes,
            out var headerOffset,
            out var primaryCount,
            out var secondaryCount,
            out var baseA,
            out var baseB,
            out var firstCntStart));
        Assert.Equal(0, headerOffset);
        Assert.Equal(2, primaryCount);
        Assert.Equal(3, secondaryCount);
        Assert.Equal(0x170, baseA);
        Assert.Equal(0x4D0, baseB);
        Assert.Equal(0x1A0, firstCntStart);

        var expected = new[]
        {
            new TextureAnchor(
                0x3B870D67u,
                128,
                128,
                "D08C8EF4D85F435D2F3A51C9E903C324C139F54523E7B34207DF8598C7949F30"),
            new TextureAnchor(
                0x7C2777B7u,
                64,
                64,
                "01B83DC9C8274BA19925C23A75F6A39E43C1263C74DB6FF0ED57490FD724FD01"),
            new TextureAnchor(
                0xDC2B4564u,
                64,
                64,
                "B3ED553204E0E6A8C39AF396CD04E09543AD4235C3E37A266CD1C58D8080031B")
        };

        var records = ThawZoneTexFile.ParseHeaderEntries(stexBytes);
        Assert.Equal(expected.Select(static anchor => anchor.Checksum),
            records.Select(static record => record.Checksum));
        Assert.DoesNotContain(records, static record => record.Checksum == 0x00005800u);

        var textures = ThawZoneTexFile.DecodeAllFromFile(stexBytes)
            .ToDictionary(static texture => texture.Checksum);
        Assert.Equal(expected.Length, textures.Count);
        foreach (var anchor in expected)
        {
            Assert.True(textures.TryGetValue(anchor.Checksum, out var texture),
                $"Missing decoded texture 0x{anchor.Checksum:X8}");
            Assert.Equal(anchor.Width, texture!.Width);
            Assert.Equal(anchor.Height, texture.Height);
            Assert.NotNull(texture.Pixels);
            Assert.Equal(anchor.RgbaSha256,
                Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
        }

        Assert.True(ZoneTextureCatalog.TryBuild(
            [new ZoneTextureCatalog.ZoneTexSource(PakPath, pakBytes, true)],
            out var catalog));
        Assert.NotNull(catalog);

        var textureHint = catalog!.FindTextureEntryHintBefore(PakPath, 0x9630);
        Assert.Equal("z_sz.pak.ps2::000018A0", textureHint);
        var resolver = catalog.CreateDebugTex0Resolver(textureHint);
        var scene = Ps2GeomFile.ParsePakMdl(mdlBytes);
        Assert.Equal(25, scene.Leaves.Count);

        var bindings = scene.Leaves
            .Select(leaf => resolver(leaf.DmaTex0, leaf.GroupChecksum))
            .ToList();
        Assert.All(bindings, binding =>
        {
            Assert.Equal(0x3B870D67u, binding.Checksum);
            Assert.Equal("entry_exact", binding.ResolveMode);
        });
    }

    private sealed record TextureAnchor(uint Checksum, int Width, int Height, string RgbaSha256);
}