using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene;

/// <summary>
///     THAW worldzone PAKs nested inside the game's DATAP.WAD must be detected
///     and convert end-to-end (geometry + sibling-PAK textures) without any
///     filesystem extraction step.
/// </summary>
public sealed class Ps2WorldzoneArchiveTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    // Properties evaluate eagerly when referenced (even inside Assert.SkipWhen(!File.Exists(...))),
    // so guard SampleBuildsDir to avoid Path.Combine throwing on CI when sample data is absent.
    private string WadPath =>
        paths.SampleBuildsDir is null ? string.Empty : Path.Combine(
            paths.SampleBuildsDir,
            ThawPs2Build,
            "DATAP.WAD");

    [Fact]
    public void WadNestedWorldzonePak_IsDetectedAndConvertsWithTextures()
    {
        Assert.SkipWhen(!File.Exists(WadPath), "THAW PS2 DATAP.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(WadPath);
        Assert.SkipWhen(backend == null, "DATAP.WAD did not open as a WAD archive");

        var entry = backend!.Entries.FirstOrDefault(e =>
            e.Name.Equals("z_bh.pak.ps2", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(entry == null, "z_bh.pak.ps2 not found inside DATAP.WAD");

        var source = new ArchiveAssetSource(backend, entry!);
        Assert.True(Ps2WorldzoneDetection.IsWorldzonePak(source.ReadBytes()),
            "Nested z_bh.pak.ps2 should be detected as a worldzone PAK");

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry!.Name,
            OutputStem = "z_bh",
            SourceKind = ModelSourceKind.Ps2Worldzone,
            Ps2SubFormat = Ps2SceneSubFormat.PakWorldzone
        });

        Assert.True(document.Meshes.Count > 0,
            "expected worldzone meshes from the WAD-nested PAK");
        Assert.True(document.Textures.Count > 0,
            "expected sibling-PAK textures to embed for the WAD-nested worldzone");
    }
}
