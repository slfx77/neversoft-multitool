using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxSplineClawLocatorTests(TestPaths paths)
{
    [Fact]
    public void Locate_RetailDocock_UsesSiblingClawPsx()
    {
        var docockPath = paths.FindSampleFile(
            "Spider-Man (2000-9-1, PSX - Final)", "docock.psx");
        Assert.SkipWhen(docockPath == null, "Spider-Man final docock.psx not available");

        var claw = PsxSplineClawLocator.Locate(new FileSystemAssetSource(docockPath!));

        Assert.NotNull(claw);
        Assert.Contains(PsxSplineClawLocator.ClawMeshHash, claw!.File.MeshNameHashes);
        Assert.NotNull(claw.TextureProvider);
    }

    [Fact]
    public void Locate_PrototypeDocock_ExtractsClawFromLevelObjectBank()
    {
        // The February 2000 prototypes ship no standalone claw.psx — the claw
        // mesh lives inside the boss arena's object bank (l8a4_o.psx). Without
        // this fallback the tentacle tips exported bare (user-reported).
        var docockPath = paths.FindSampleFile(
            "Spider-Man (2000-2-18, PSX - Prototype)", "docock.psx");
        Assert.SkipWhen(docockPath == null, "Spider-Man 2/18 prototype docock.psx not available");

        var claw = PsxSplineClawLocator.Locate(new FileSystemAssetSource(docockPath!));

        Assert.NotNull(claw);
        var mesh = Assert.Single(claw!.File.Meshes);
        Assert.Equal([PsxSplineClawLocator.ClawMeshHash], claw.File.MeshNameHashes);
        Assert.True(mesh.Faces.Count > 0);
        // Local space: the writer applies the spline tip transform itself, so
        // the repackaged bank object must not carry the bank's world position.
        var obj = Assert.Single(claw.File.Objects);
        Assert.Equal(0, obj.RawX);
        Assert.Equal(0, obj.RawY);
        Assert.Equal(0, obj.RawZ);
        Assert.NotNull(claw.TextureProvider);
    }
}
