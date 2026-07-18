using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class SpiderManPrototypeVisibilityRegressionTests(TestPaths paths)
{
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
        Assert.Equal(4_179, combined.TriangleCount);
        Assert.Equal(61, combined.Nodes.Count);
        Assert.Equal(8, combined.Nodes.Count(
            static node => node.Name.StartsWith("objects_", StringComparison.Ordinal)));
        Assert.DoesNotContain(combined.Nodes,
            static node => node.Name is "objects_001" or "objects_002" or
                "objects_010" or "objects_011");

        var rotatingBankPiece = Assert.Single(combined.Nodes,
            static node => node.Name == "objects_003");
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
            combined.Nodes.Where(
                static node => node.Name.StartsWith("objects_", StringComparison.Ordinal)),
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
            includeLevelObjects: false);

        Assert.Equal(3_549, document.TriangleCount);
        Assert.Equal(53, document.Nodes.Count);
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

        Assert.Equal(3_549, document.TriangleCount);
        Assert.Equal(53, document.Nodes.Count);
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

    public enum ObjectCompanionBehavior
    {
        Missing,
        Malformed,
        ReadFailure
    }
}
