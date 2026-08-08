using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the N64 coplanar-decal split (2026-08-07). The ports carry the PS1's
///     authored decals — faces sitting exactly on the surface they mark, which
///     the PS1 sequences through its ordering table and the RDP resolves with a
///     decal render mode — so they arrive coplanar and z-fight once exported to
///     glTF. THPS1 Downtown's street lines are the reported case.
/// </summary>
public sealed class N64CoplanarOverlayTests(TestPaths paths)
{
    private const string Thps1N64Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater (USA).z64";

    private ModelDocument ParseBundle(string slot, out IArchiveFileSystem fs)
    {
        var romPath = paths.FindSampleFile(Thps1N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS1 N64 ROM sample not available");
        fs = ArchiveFileSystem.TryOpen(romPath!)!;
        var backend = ArchiveAssetBackend.TryOpen(romPath!)!;
        var entry = N64Bundles.FindBundle(backend, slot);
        var source = new ArchiveAssetSource(backend, entry);

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = "n64_overlay",
            SourceKind = ModelSourceKind.N64Model
        });
    }

    private static IEnumerable<(ModelMesh Mesh, MeshDrawOrderMetadata Order)> Overlays(ModelDocument document)
    {
        foreach (var mesh in document.Meshes)
        {
            if (!mesh.Name.Contains("__overlay", StringComparison.Ordinal))
                continue;
            var order = mesh.Primitives
                .SelectMany(static primitive => primitive.NativeMetadata)
                .OfType<MeshDrawOrderMetadata>()
                .FirstOrDefault();
            if (order != null)
                yield return (mesh, order);
        }
    }

    /// <summary>The outward normal of a mesh's first non-degenerate triangle.</summary>
    private static Vector3 OutwardNormal(ModelMesh mesh)
    {
        foreach (var primitive in mesh.Primitives)
        {
            for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
            {
                var a = primitive.Vertices[primitive.Indices[i]].Position;
                var b = primitive.Vertices[primitive.Indices[i + 1]].Position;
                var c = primitive.Vertices[primitive.Indices[i + 2]].Position;
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.Length() > 1e-5f)
                    return Vector3.Normalize(normal);
            }
        }

        return Vector3.Zero;
    }

    [Fact]
    public void Downtown_SplitsItsDecalsIntoDrawOrderedOverlayMeshes()
    {
        var document = ParseBundle("004", out var fs);
        using var _ = fs;

        var overlays = Overlays(document).ToList();
        Assert.NotEmpty(overlays);
        Assert.All(overlays, item => Assert.True(item.Order.DrawIndex >= 1));
        Assert.All(overlays, item => Assert.Equal(item.Order.DrawIndex, item.Order.PassIndex));
    }

    /// <summary>
    ///     The separation must push a decal OUT of the surface it covers. A sign
    ///     error here buries it instead, which is visibly worse than the
    ///     z-fighting being fixed — hence zero tolerance. The N64 writer emits
    ///     corners unmodified, so the outward normal is cross(p1-p0, p2-p0);
    ///     the PS1 writer's opposite convention would invert every decal.
    /// </summary>
    [Fact]
    public void OverlayLift_PointsOutOfTheSurface()
    {
        var document = ParseBundle("004", out var fs);
        using var _ = fs;

        var overlays = Overlays(document).ToList();
        Assert.NotEmpty(overlays);
        foreach (var (mesh, order) in overlays)
        {
            var offset = new Vector3(order.BlendOffsetX, order.BlendOffsetY, order.BlendOffsetZ);
            Assert.True(offset.LengthSquared() > 1e-12f, $"{mesh.Name} carries no separation");
            Assert.True(
                Vector3.Dot(Vector3.Normalize(offset), OutwardNormal(mesh)) > 0.9f,
                $"{mesh.Name} lifts into its surface instead of out of it");
        }
    }

    /// <summary>
    ///     The split must be a PARTITION: every triangle lands in exactly one
    ///     layer, so the model's triangle count cannot move. If it does, the
    ///     layer lookup is dropping or duplicating geometry and every other
    ///     measurement here is meaningless.
    /// </summary>
    [Theory]
    [InlineData("004", 9875)]
    [InlineData("014", 10781)]
    [InlineData("061", 264)]
    public void OverlaySplit_LeavesTriangleCountsUnchanged(string bundle, int expected)
    {
        var document = ParseBundle(bundle, out var fs);
        using var _ = fs;
        Assert.Equal(expected, document.TriangleCount);
    }

    /// <summary>
    ///     The medals are 52 coplanar pairs of which ZERO are same-facing — they
    ///     are two-sided sheets built from opposed single-sided triangles, which
    ///     backface culling already resolves. Flagging them would move geometry
    ///     for nothing, and this is the fixture that catches an inverted
    ///     same-facing gate.
    /// </summary>
    [Fact]
    public void Medals_HaveNoOverlaysBecauseTheirCoplanarPairsFaceOppositeWays()
    {
        var document = ParseBundle("061", out var fs);
        using var _ = fs;
        Assert.Empty(Overlays(document));
        Assert.DoesNotContain(document.Meshes, m => m.Name.Contains("__overlay", StringComparison.Ordinal));
    }
}
