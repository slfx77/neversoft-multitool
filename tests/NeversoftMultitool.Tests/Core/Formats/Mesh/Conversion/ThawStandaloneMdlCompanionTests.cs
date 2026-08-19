using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     C2/D2 regression (2026-08-19): standalone pak MDL texture companions.
///     The zone-family v6 TEX dictionary shares its version-6 header with the
///     skin-companion scene TEX, and <c>ThawSceneTexFile.Parse</c> "succeeded"
///     on it first — scrambling most of z_mainmenu's 84 backdrop textures.
///     <c>BuildPs2TextureProvider</c> must route zone dictionaries through the
///     exact zone decoder; the nearest-preceding companion rule itself was
///     correct all along (one .tex directly precedes each backdrop MDL).
/// </summary>
public sealed class ThawStandaloneMdlCompanionTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [CorpusFact]
    public void ZMainMenuNet_BackdropMdl_ResolvesAndDecodesItsZoneTexCompanion()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_mainmenu_net.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_mainmenu_net.pak.ps2 sample not available");

        var backend = ArchiveAssetBackend.TryOpen(pakPath!);
        Assert.SkipWhen(backend == null, "z_mainmenu_net.pak.ps2 did not open as an archive");
        var mdlEntry = backend!.Entries.FirstOrDefault(static e =>
            e.Name.Equals("0007B1B0.mdl", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(mdlEntry == null, "entry 0007B1B0.mdl not found in the pak");

        // In-archive companion selection (the GUI path): the nearest preceding
        // texture entry is the zone TEX dictionary 00000B40.tex.
        var source = new ArchiveAssetSource(backend, mdlEntry!);
        var companion = MeshCompanionResolver.ReadTextureCompanion(
            source, "0007B1B0", [".tex.ps2", ".tex", ".img.ps2", ".stex", ".img"], []);
        Assert.NotNull(companion);
        Assert.True(ThawZoneTexFile.IsThawZoneTex(companion),
            "companion should be the zone TEX dictionary");

        // Decode routing: the provider's pixels must be the zone decoder's,
        // not the scene-TEX misparse.
        var reference = ThawZoneTexFile.DecodeAllFromFile(companion!)
            .Where(static texture => texture.Pixels != null)
            .ToDictionary(static texture => texture.Checksum);
        Assert.Equal(84, reference.Count);

        var provider = MeshCompanionResolver.BuildPs2TextureProvider(companion);
        Assert.NotNull(provider);
        foreach (var (checksum, texture) in reference)
        {
            var png = provider!(checksum);
            Assert.NotNull(png);
            var expected = NeversoftMultitool.Core.BinaryIO.ImageWriter.WritePngToMemory(
                texture.Width, texture.Height, texture.Pixels!);
            Assert.Equal(expected, png);
        }
    }
}
