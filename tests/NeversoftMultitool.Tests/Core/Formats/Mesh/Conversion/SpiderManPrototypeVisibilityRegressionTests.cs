using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class SpiderManPrototypeVisibilityRegressionTests(TestPaths paths)
{
    public enum ObjectCompanionBehavior
    {
        Missing,
        Malformed,
        ReadFailure
    }

    private const string BuildName = "Spider-Man (2000-2-18, PSX - Prototype)";
    private const string GeometryEntryName = "l1a2_g.psx";
    private const string ObjectEntryName = "l1a2_o.psx";

    [Fact]
    public void L1A2_GeometryEntry_AssemblesLevelObjectCompanion()
    {
        var fixture = OpenFixture();
        Assert.SkipWhen(fixture == null, "Spider-Man prototype CD.WAD sample not available");
        using var fileSystem = fixture!.Value.Backend.FileSystem;

        var combined = ParseDocument(fixture.Value.GeometrySource, GeometryEntryName);
        var objects = ParseDocument(fixture.Value.ObjectSource, ObjectEntryName);

        Assert.Equal(1_155, objects.TriangleCount);
        // Re-pinned 2026-07-23: the POWERUP placement layer adds l1a2's 7
        // pickups (5 web cartridges + 2 grey gears, 72 tris each = +504) from
        // items.psx as 7 items_* nodes.
        // Re-pinned 2026-07-28: near-equal coplanar baked light/shadow pairs
        // now split into draw-order overlay nodes (triangles unchanged).
        Assert.Equal(5_432, combined.TriangleCount);
        // Re-pinned 2026-07-30: the near-equal overlay branch now requires real
        // shared area (exact coplanar polygon clip), dropping the edge-adjacent
        // false flags that had inflated the split count (271 → 77). l1a2's 5
        // surviving overlay faces all come from the SMALL-DECAL branch — the
        // near-equal branch fires zero times on this file (915 near-equal pairs
        // reach it and none has genuine shared area; its 62 same-texture exact
        // twins are skipped by design), so this pin does not constrain that
        // branch. Verified by an exact-intersection census of the file.
        // Re-pinned 2026-08-04 (77 → 128): semi-transparent faces now export
        // as per-face __blend nodes re-based on their centroids (three.js
        // sorts transparent objects by node origin; triangles unchanged).
        Assert.Equal(128, combined.Nodes.Count);
        // Re-pinned 2026-07-28: the TRG BackgroundCreate join reclassifies
        // l1a2's three background layers as camera-locked sky nodes
        // (sky__objects_*), leaving 11 ordinary bank objects (draw-order
        // overlay splits carry an __overlay suffix, per-face semi-transparent
        // splits a __blend suffix — both counted apart).
        // Re-pinned 2026-08-04 (11 → 10): one bank object is ENTIRELY
        // semi-transparent, so its plain node dissolved into __blend
        // per-face nodes (asserted below); the other ten are unchanged.
        Assert.Equal(10,
            combined.Nodes.Count(static node =>
                node.Name.StartsWith("objects_", StringComparison.Ordinal)
                && !node.Name.Contains("__overlay", StringComparison.Ordinal)
                && !node.Name.Contains("__blend", StringComparison.Ordinal)));
        Assert.Contains(combined.Nodes, static node =>
            node.Name.StartsWith("objects_", StringComparison.Ordinal)
            && node.Name.Contains("__blend", StringComparison.Ordinal));
        Assert.Equal(3,
            combined.Nodes.Count(static node =>
                node.Name.StartsWith("sky__objects_", StringComparison.Ordinal)
                && !node.Name.Contains("__overlay", StringComparison.Ordinal)));
        // Bank objects without any TRG platform reference (the majority case
        // corpus-wide) place at their stored bank positions. Objects 2/10/11
        // are the level's TRG-registered background layers — camera-locked
        // sky nodes anchored at the registering node, not bank placements.
        Assert.Contains(combined.Nodes, static node => node.Name == "objects_001");
        Assert.Contains(combined.Nodes, static node => node.Name == "sky__objects_002");
        Assert.Contains(combined.Nodes, static node => node.Name == "sky__objects_010");
        Assert.Contains(combined.Nodes, static node => node.Name == "sky__objects_011");

        // Object 3's PLATFORM node re-instances the model away from its bank
        // home, so the rotated copy carries the trigger-node suffix while the
        // bank's own instance keeps the plain name.
        Assert.Contains(combined.Nodes, static node => node.Name == "objects_003");
        var rotatingBankPiece = Assert.Single(combined.Nodes,
            static node => node.Name == "objects_003_node_015");
        Assert.Equal(-6_476f, rotatingBankPiece.Transform.Translation.X, 3);
        Assert.Equal(2_646.222f, rotatingBankPiece.Transform.Translation.Y, 3);
        Assert.Equal(109.333f, rotatingBankPiece.Transform.Translation.Z, 3);
        Assert.Equal(0.259313f, rotatingBankPiece.Transform.M11, 5);
        Assert.Equal(-0.965793f, rotatingBankPiece.Transform.M13, 5);
        Assert.Equal(0.965793f, rotatingBankPiece.Transform.M31, 5);
        Assert.Equal(0.259313f, rotatingBankPiece.Transform.M33, 5);

        var upperBankPiece = Assert.Single(combined.Nodes,
            static node => node.Name == "objects_007");
        Assert.Equal(-7_111.111f, upperBankPiece.Transform.Translation.X, 3);
        Assert.Equal(2_844.444f, upperBankPiece.Transform.Translation.Y, 3);
        Assert.Equal(-711.111f, upperBankPiece.Transform.Translation.Z, 3);
        Assert.All(
            combined.Nodes.Where(static node => node.Name.StartsWith("objects_", StringComparison.Ordinal)),
            node =>
            {
                Assert.NotNull(node.MeshIndex);
                Assert.All(
                    combined.Meshes[node.MeshIndex!.Value].Primitives,
                    primitive => Assert.NotNull(
                        combined.Materials[primitive.MaterialIndex].TextureIndex));
            });
        Assert.Equal(
            combined.Nodes.Count,
            combined.Nodes.Select(static node => node.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(combined.Textures,
            static texture => texture.NativeChecksum == 0xA7E28C48u);
        Assert.Equal(2, combined.VisibilityGroups.Count);
        Assert.All(combined.VisibilityGroups, static group => Assert.True(group.IsEnabled));
    }

    [Fact]
    public void L1A2_GeometryEntry_CanExcludeLevelObjectCompanion()
    {
        var fixture = OpenFixture();
        Assert.SkipWhen(fixture == null, "Spider-Man prototype CD.WAD sample not available");
        using var fileSystem = fixture!.Value.Backend.FileSystem;

        var document = ParseDocument(
            fixture.Value.GeometrySource,
            GeometryEntryName,
            false);

        Assert.Equal(3_549, document.TriangleCount);
        // Re-pinned 2026-07-29: near-equal overlays require interior overlap
        // (225 → 55, edge-adjacent split nodes dropped; triangles unchanged).
        // Re-pinned 2026-08-04 (55 → 87): per-face __blend ST split.
        Assert.Equal(87, document.Nodes.Count);
        Assert.DoesNotContain(document.Nodes,
            static node => node.Name.StartsWith("objects_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ObjectCompanionBehavior.Missing)]
    [InlineData(ObjectCompanionBehavior.Malformed)]
    [InlineData(ObjectCompanionBehavior.ReadFailure)]
    public void L1A2_UnavailableOrMalformedObjectCompanion_KeepsPrimaryGeometry(
        ObjectCompanionBehavior behavior)
    {
        var fixture = OpenFixture();
        Assert.SkipWhen(fixture == null, "Spider-Man prototype CD.WAD sample not available");
        using var fileSystem = fixture!.Value.Backend.FileSystem;

        var source = new ObjectCompanionOverrideSource(
            fixture.Value.GeometrySource,
            behavior);
        var document = ParseDocument(source, GeometryEntryName);

        // The POWERUP layer is independent of the *_o bank, so an unavailable /
        // malformed / unreadable bank still places l1a2's 7 pickups from
        // items.psx (+504 tris, +7 items_* nodes). Only the bank's own
        // "objects_" nodes are absent.
        Assert.Equal(4_053, document.TriangleCount);
        // Re-pinned 2026-07-29: near-equal overlays require interior overlap
        // (232 → 62, edge-adjacent split nodes dropped; triangles unchanged).
        // Re-pinned 2026-08-04 (62 → 108): per-face __blend ST split.
        Assert.Equal(108, document.Nodes.Count);
        Assert.DoesNotContain(document.Nodes,
            static node => node.Name.StartsWith("objects_", StringComparison.Ordinal));
    }

    private (ArchiveAssetBackend Backend, AssetSource GeometrySource, AssetSource ObjectSource)? OpenFixture()
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        if (wadPath == null)
            return null;

        var backend = ArchiveAssetBackend.TryOpen(wadPath);
        if (backend == null)
            return null;

        var geometryEntry = backend.FindEntry(GeometryEntryName);
        var objectEntry = backend.FindEntry(ObjectEntryName);
        if (geometryEntry == null || objectEntry == null)
        {
            backend.FileSystem.Dispose();
            return null;
        }

        return (
            backend,
            new ArchiveAssetSource(backend, geometryEntry),
            new ArchiveAssetSource(backend, objectEntry));
    }

    private static ModelDocument ParseDocument(
        AssetSource source,
        string fileName,
        bool includeLevelObjects = true)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = fileName,
            OutputStem = Path.GetFileNameWithoutExtension(fileName),
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = includeLevelObjects
        });
    }

    private sealed class ObjectCompanionOverrideSource(
        AssetSource inner,
        ObjectCompanionBehavior behavior) : AssetSource
    {
        public override string DisplayName => inner.DisplayName;
        public override string EntryName => inner.EntryName;
        public override string? FileSystemPath => inner.FileSystemPath;

        public override byte[] ReadBytes()
        {
            return inner.ReadBytes();
        }

        public override bool CompanionExists(string nameWithExtension)
        {
            return IsObjectCompanion(nameWithExtension)
                ? behavior != ObjectCompanionBehavior.Missing
                : inner.CompanionExists(nameWithExtension);
        }

        public override byte[]? TryReadCompanion(string nameWithExtension)
        {
            return IsObjectCompanion(nameWithExtension)
                ? ReadObjectCompanion()
                : inner.TryReadCompanion(nameWithExtension);
        }

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            if (string.Equals(stem, Path.GetFileNameWithoutExtension(ObjectEntryName),
                    StringComparison.OrdinalIgnoreCase)
                && extensions.Any(static extension =>
                    extension.Equals(".psx", StringComparison.OrdinalIgnoreCase)))
            {
                return ReadObjectCompanion();
            }

            return inner.TryReadCompanion(stem, extensions, subdirs);
        }

        private static bool IsObjectCompanion(string nameWithExtension)
        {
            return string.Equals(
                Path.GetFileName(nameWithExtension),
                ObjectEntryName,
                StringComparison.OrdinalIgnoreCase);
        }

        private byte[]? ReadObjectCompanion()
        {
            return behavior switch
            {
                ObjectCompanionBehavior.Missing => null,
                ObjectCompanionBehavior.Malformed => [0x01, 0x02, 0x03],
                ObjectCompanionBehavior.ReadFailure => throw new InvalidDataException(
                    "Synthetic companion read failure"),
                _ => throw new ArgumentOutOfRangeException(nameof(behavior))
            };
        }
    }
}