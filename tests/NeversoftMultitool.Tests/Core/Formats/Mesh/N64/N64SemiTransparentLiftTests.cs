using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the N64 blanket semi-transparent lift (2026-08-07) — the PS1
///     writer's other half of decal resolution, and the fix for THPS1
///     Downtown's street lines z-fighting the road.
///     <para>
///         The coplanar detector deliberately skips any pair with a
///         semi-transparent member, exactly as the PS1 detector does, because
///         those faces are separated geometrically instead. Without the lift
///         that exclusion leaves a real defect standing: Downtown's decals are
///         semi-transparent, so the detector correctly ignored them and nothing
///         else moved them.
///     </para>
/// </summary>
public sealed class N64SemiTransparentLiftTests(TestPaths paths)
{
    private const string Thps1N64Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater (USA).z64";

    /// <summary>
    ///     Plane bucket width for the measurement below, in export units. One
    ///     THPS1 raw unit is 1/2.25 ≈ 0.44 export units, so this cannot merge
    ///     two authored planes while it does absorb float noise.
    /// </summary>
    private const float PlaneTolerance = 0.05f;

    private const float NormalTolerance = 0.02f;

    /// <summary>
    ///     Largest plane bucket compared pairwise. Downtown's road tessellates
    ///     into hundreds of coplanar triangles and this measurement is O(n²)
    ///     within a bucket; the cap keeps the test quick. It is a measurement
    ///     limit only — the shipped detector uses a sweep and has no cap.
    /// </summary>
    private const int MaximumBucketSize = 512;

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
            OutputStem = "n64_lift",
            SourceKind = ModelSourceKind.N64Model
        });
    }

    private sealed record ExportedTriangle(Vector3[] Points, Vector3 Normal, bool SemiTransparent);

    /// <summary>
    ///     Every exported triangle in world space, tagged with whether its
    ///     material blends. The <c>__st</c> suffix is only attached to materials
    ///     that really composite, which is exactly the set that cannot win a
    ///     depth tie.
    /// </summary>
    private static List<ExportedTriangle> ExportedTriangles(ModelDocument document)
    {
        var placement = new Dictionary<int, Vector3>();
        foreach (var node in document.Nodes)
        {
            if (node.MeshIndex is { } index)
                placement.TryAdd(index, node.Transform.Translation);
        }

        var triangles = new List<ExportedTriangle>();
        for (var meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
        {
            var offset = placement.TryGetValue(meshIndex, out var translation) ? translation : Vector3.Zero;
            foreach (var primitive in document.Meshes[meshIndex].Primitives)
                AddPrimitiveTriangles(document, primitive, offset, triangles);
        }

        return triangles;
    }

    private static void AddPrimitiveTriangles(
        ModelDocument document, ModelPrimitive primitive, Vector3 offset, List<ExportedTriangle> triangles)
    {
        var name = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < document.Materials.Count
            ? document.Materials[primitive.MaterialIndex].Name
            : string.Empty;
        var semi = name.Contains("__st", StringComparison.Ordinal);

        for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
        {
            Vector3[] points =
            [
                primitive.Vertices[primitive.Indices[i]].Position + offset,
                primitive.Vertices[primitive.Indices[i + 1]].Position + offset,
                primitive.Vertices[primitive.Indices[i + 2]].Position + offset
            ];
            var normal = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
            if (normal.Length() > 1e-6f)
                triangles.Add(new ExportedTriangle(points, Vector3.Normalize(normal), semi));
        }
    }

    /// <summary>
    ///     Same-facing coplanar pairs that really overlap — the ones that
    ///     z-fight. Opposite-facing pairs are two-sided sheets and are excluded,
    ///     the same gate the shipped detector applies.
    /// </summary>
    private static (int Total, int InvolvingSemiTransparent) CoplanarOverlaps(
        List<ExportedTriangle> triangles)
    {
        var buckets = new Dictionary<(int, int, int, int), List<ExportedTriangle>>();
        foreach (var triangle in triangles)
        {
            var key = PlaneKey(triangle);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                buckets[key] = bucket;
            }

            bucket.Add(triangle);
        }

        var total = 0;
        var semi = 0;
        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2 || bucket.Count > MaximumBucketSize)
                continue;

            var (bucketTotal, bucketSemi) = CountOverlaps(bucket);
            total += bucketTotal;
            semi += bucketSemi;
        }

        return (total, semi);
    }

    private static (int Total, int InvolvingSemiTransparent) CountOverlaps(List<ExportedTriangle> bucket)
    {
        var total = 0;
        var semi = 0;
        for (var i = 0; i < bucket.Count; i++)
        {
            for (var j = i + 1; j < bucket.Count; j++)
            {
                // Only SAME-FACING pairs can fight; an opposed pair is a
                // two-sided sheet that backface culling resolves.
                if (Vector3.Dot(bucket[i].Normal, bucket[j].Normal) <= 0f)
                    continue;
                if (!CoplanarOverlayGeometry.HasInteriorOverlap(bucket[i].Points, bucket[j].Points))
                    continue;

                total++;
                if (bucket[i].SemiTransparent || bucket[j].SemiTransparent)
                    semi++;
            }
        }

        return (total, semi);
    }

    /// <summary>
    ///     Plane bucket key. Antiparallel normals fold together so a sheet lands
    ///     with its own back face; <see cref="CountOverlaps" /> separates them
    ///     again.
    /// </summary>
    private static (int, int, int, int) PlaneKey(ExportedTriangle triangle)
    {
        var normal = triangle.Normal;
        var folded = DominantComponent(normal) < 0 ? -normal : normal;
        return (
            (int)MathF.Round(folded.X / NormalTolerance),
            (int)MathF.Round(folded.Y / NormalTolerance),
            (int)MathF.Round(folded.Z / NormalTolerance),
            (int)MathF.Round(Vector3.Dot(folded, triangle.Points[0]) / PlaneTolerance));
    }

    private static float DominantComponent(Vector3 value)
    {
        if (MathF.Abs(value.X) >= MathF.Abs(value.Y) && MathF.Abs(value.X) >= MathF.Abs(value.Z))
            return value.X;
        return MathF.Abs(value.Y) >= MathF.Abs(value.Z) ? value.Y : value.Z;
    }

    /// <summary>
    ///     The reported defect, measured. Before the lift Downtown exported 539
    ///     same-facing coplanar overlapping pairs (per
    ///     <c>tools/diagnostics/n64_coplanar_probe.py --limit 512</c>), 436 of
    ///     them a street line fighting the road it is painted on. Every
    ///     semi-transparent one is gone.
    ///     <para>
    ///         The residue is opaque near-equal pairs, which the detector
    ///         deliberately leaves alone: its size branch only claims a face
    ///         genuinely smaller than its partner, and the PS1's
    ///         ordering-table tie-break for equals does not transfer to a
    ///         console with a z-buffer. The count is pinned so that residue
    ///         cannot grow unnoticed. It reads 63 here against the probe's 60
    ///         because the probe additionally skips pairs sharing both mesh and
    ///         material as ordinary tessellation, which this does not.
    ///     </para>
    /// </summary>
    [Fact]
    public void Downtown_HasNoSemiTransparentFaceLeftFightingItsSurface()
    {
        var document = ParseBundle("004", out var fs);
        using var _ = fs;

        var (total, semiTransparent) = CoplanarOverlaps(ExportedTriangles(document));

        Assert.Equal(0, semiTransparent);
        Assert.True(total <= 63, $"opaque coplanar residue grew to {total}, was 63");
    }

    /// <summary>
    ///     A lone semi-transparent face has no neighbour to average with, so it
    ///     rises along its own outward normal by exactly the magnitude asked
    ///     for. This is the case that catches an inverted sign — burying a decal
    ///     is visibly worse than the z-fighting it replaces.
    /// </summary>
    [Fact]
    public void ALoneSemiTransparentFace_RisesAlongItsOwnNormal()
    {
        Vector3[] points = [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)];
        var lift = N64SemiTransparentLift.Build([Candidate(0, points, semi: true)], magnitude: 0.25f);

        Assert.NotNull(lift);
        var expected = Vector3.Normalize(Vector3.Cross(points[1] - points[0], points[2] - points[0])) * 0.25f;
        Assert.Equal(expected.X, lift!.OffsetFor(points[0], Vector3.UnitX).X, 5);
        Assert.Equal(expected.Y, lift.OffsetFor(points[0], Vector3.UnitX).Y, 5);
        Assert.Equal(expected.Z, lift.OffsetFor(points[0], Vector3.UnitX).Z, 5);
    }

    /// <summary>
    ///     Two semi-transparent faces meeting at an angle must move their SHARED
    ///     corners identically, or the surface tears open along the seam — which
    ///     is what per-face directions did to Spider-Man's all-semi-transparent
    ///     webdome on the PS1 side. The corner they share is averaged; the
    ///     corners they do not keep their own face's direction.
    /// </summary>
    [Fact]
    public void AdjacentSemiTransparentFaces_MoveTheirSharedCornerTogether()
    {
        var shared = new Vector3(0, 0, 0);
        var alsoShared = new Vector3(1, 0, 0);
        Vector3[] flat = [shared, alsoShared, new(0, 0, 1)];
        Vector3[] tilted = [shared, alsoShared, new(0, 1, -1)];

        var lift = N64SemiTransparentLift.Build(
            [Candidate(0, flat, semi: true), Candidate(1, tilted, semi: true)], magnitude: 0.25f);
        Assert.NotNull(lift);

        // Whatever direction each face would have chosen alone, the shared
        // corners resolve to one vector, so the seam stays closed.
        var flatNormal = Vector3.Normalize(Vector3.Cross(flat[1] - flat[0], flat[2] - flat[0]));
        var tiltedNormal = Vector3.Normalize(Vector3.Cross(tilted[1] - tilted[0], tilted[2] - tilted[0]));
        Assert.True(Vector3.Dot(flatNormal, tiltedNormal) < 0.99f, "fixture faces must differ in facing");

        Assert.Equal(lift!.OffsetFor(shared, flatNormal), lift.OffsetFor(shared, tiltedNormal));
        Assert.Equal(lift.OffsetFor(alsoShared, flatNormal), lift.OffsetFor(alsoShared, tiltedNormal));
    }

    /// <summary>
    ///     Opaque geometry contributes nothing and nothing lifts — a model with
    ///     no semi-transparent faces (most characters) must export at its
    ///     authored positions.
    /// </summary>
    [Fact]
    public void AnOpaqueModel_BuildsNoLiftAtAll()
    {
        Vector3[] points = [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)];
        Assert.Null(N64SemiTransparentLift.Build([Candidate(0, points, semi: false)], magnitude: 0.25f));
    }

    private static N64OverlayCandidateSource Candidate(int index, Vector3[] points, bool semi)
    {
        return new N64OverlayCandidateSource(
            new N64TriangleInstanceKey(0, index),
            points,
            TextureSlot: 1,
            FaceFlags: semi ? PsxFaceFlags.SemiTransparent : (ushort)0);
    }
}
