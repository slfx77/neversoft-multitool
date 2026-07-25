using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene;

/// <summary>
///     THAW PAKs frequently strip asset names, leaving offset-generated names
///     such as 000061B0.stex + 0000EEC0.mdl. These cases must resolve their
///     package-local texture when selected directly from DATAP.WAD.
/// </summary>
public sealed class Ps2ArchiveMeshTextureResolutionTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    private string WadPath => paths.SampleBuildsDir is null
        ? string.Empty
        : Path.Combine(paths.SampleBuildsDir, ThawPs2Build, "DATAP.WAD");

    [Theory]
    [InlineData(
        "skies/cap_shell1_sky/cap_shell1_sky.pak.ps2",
        "00011700.mdl",
        2,
        3)]
    [InlineData(
        "cutscenes/bh_levelevent/ps2/bh_levelevent_main/bh_levelevent_main.pak.ps2",
        "0000EEC0.mdl",
        1,
        12)]
    [InlineData(
        "cutscenes/bh_levelevent/ps2/bh_levelevent_main/bh_levelevent_main.pak.ps2",
        "00108230.skin",
        1,
        1)]
    public void OffsetNamedMesh_UsesNearestPrecedingPackageTexture(
        string pakPath,
        string meshName,
        int minimumTextures,
        int minimumBoundMaterials)
    {
        Assert.SkipWhen(!File.Exists(WadPath), "THAW PS2 DATAP.WAD sample not available");

        var wad = ArchiveAssetBackend.TryOpen(WadPath);
        Assert.SkipWhen(wad == null, "DATAP.WAD did not open as a WAD archive");
        ArchiveAssetBackend? pak = null;
        try
        {
            var pakEntry = wad!.FindByPath(pakPath);
            Assert.NotNull(pakEntry);
            pak = wad.TryOpenNested(pakEntry!);
            Assert.NotNull(pak);

            var meshEntry = pak!.FindEntry(meshName);
            Assert.NotNull(meshEntry);
            var source = new ArchiveAssetSource(pak, meshEntry!);
            var data = source.ReadBytes();
            var subFormat = GetSubFormat(data);

            var document = new MeshModelParser().Parse(new MeshImportRequest
            {
                Source = source,
                FileName = meshEntry!.Name,
                OutputStem = Path.GetFileNameWithoutExtension(meshEntry.Name),
                SourceKind = ModelSourceKind.Ps2Scene,
                Ps2SubFormat = subFormat
            });

            Assert.NotEmpty(document.Meshes);
            Assert.True(document.Textures.Count >= minimumTextures,
                $"expected at least {minimumTextures} package-local texture(s), got {document.Textures.Count}");
            var boundMaterials = document.Materials.Count(static material => material.TextureIndex != null);
            Assert.True(boundMaterials >= minimumBoundMaterials,
                $"expected at least {minimumBoundMaterials} textured material(s), got {boundMaterials}");
        }
        finally
        {
            pak?.FileSystem.Dispose();
            wad?.FileSystem.Dispose();
        }
    }

    private static Ps2SceneSubFormat GetSubFormat(byte[] data)
    {
        if (ThawPs2SkinFile.IsPakSkin(data))
            return Ps2SceneSubFormat.PakSkin;
        if (Ps2GeomFile.IsPakMdl(data))
            return Ps2SceneSubFormat.PakMdl;
        if (ThawPs2SkinFile.IsThawPs2Skin(data, data.Length))
            return Ps2SceneSubFormat.ThawSkin;

        throw new InvalidDataException("Fixture is not a supported THAW PS2 package mesh.");
    }
}