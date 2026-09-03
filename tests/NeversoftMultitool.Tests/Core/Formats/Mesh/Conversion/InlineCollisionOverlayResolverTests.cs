using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class InlineCollisionOverlayResolverTests(TestPaths paths)
{
    private const string Thps2PsxBuild =
        "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private const string ApocalypsePsxBuild =
        "Apocalypse (1998-11-17, PSX - Final)";
    private const string SpiderManDcBuild =
        "Spider-Man (2001-2-14, DC - Prototype)";
    private const string Thps3Ps2Build =
        "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    [Fact]
    public void PsxLevelGeometryRole_ComposesInlineCollisionWithoutAnObjectBank()
    {
        var level = CreatePsxLevel();
        var source = new StubAssetSource(
            "l1a2a_g.psx",
            ("l1a2a_t.trg", BuildV21SpoolEnvironmentTrg("l1a2a_g")));
        var document = ModelDocument.CreateNative(
            "l1a2a_g",
            ModelSourceKind.Psx,
            new PsxNativeSource(level, static _ => null, null));

        Assert.True(CollisionOverlayResolver.TryPopulate(
            document, source, source.EntryName, ModelSourceKind.Psx));
        Assert.Equal(1, document.TriangleCount);
        Assert.Equal(0x0510, Assert.Single(
            Assert.Single(Assert.Single(document.Meshes).Primitives)
                .NativeMetadata.OfType<PsxCollisionFlagsRenderMetadata>()).CollisionFlags);
        var overlay = Assert.Single(
            document.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        Assert.Equal("l1a2a_g.psx", overlay.CompanionName);
        Assert.Equal(1, overlay.ObjectCount);
        Assert.Equal(1, overlay.TriangleCount);

        Assert.False(CollisionOverlayResolver.TryPopulate(
            document, source, source.EntryName, ModelSourceKind.Psx));
        Assert.Single(document.Meshes);
    }

    [Fact]
    public void PsxArbitraryMeshName_IsNotPromotedToALevelCollisionSurface()
    {
        var level = CreatePsxLevel();
        var source = new StubAssetSource("spidey.psx");
        var document = ModelDocument.CreateNative(
            "spidey",
            ModelSourceKind.Psx,
            new PsxNativeSource(level, static _ => null, null));

        Assert.False(CollisionOverlayResolver.TryPopulate(
            document, source, source.EntryName, ModelSourceKind.Psx));
        Assert.Empty(document.Materials);
        Assert.Empty(document.Meshes);
        Assert.Empty(document.Nodes);
        Assert.Empty(document.Scenes);
        Assert.Empty(document.NativeMetadata);
        Assert.Equal(0, document.TriangleCount);
    }

    [Fact]
    public void RenderWareBsp_ComposesOnlyCompleteInlineTriangleFlags()
    {
        var source = new StubAssetSource("Burn.bsp");
        var validWorld = CreateRwWorld([0x04C0]);
        var valid = ModelDocument.CreateNative(
            "Burn",
            ModelSourceKind.RenderWareBsp,
            new RenderWareBspNativeSource(validWorld, null));

        Assert.True(CollisionOverlayResolver.TryPopulate(
            valid, source, source.EntryName, ModelSourceKind.RenderWareBsp));
        Assert.Equal(1, valid.TriangleCount);
        Assert.Equal(0x04C0, Assert.Single(
            Assert.Single(Assert.Single(valid.Meshes).Primitives)
                .NativeMetadata.OfType<RwBspCollisionFlagsRenderMetadata>()).CollisionFlags);
        var overlay = Assert.Single(
            valid.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        Assert.Equal("Burn.bsp", overlay.CompanionName);
        Assert.Equal(1, overlay.ObjectCount);
        Assert.Equal(1, overlay.TriangleCount);

        var incompleteWorld = CreateRwWorld([]);
        var incomplete = ModelDocument.CreateNative(
            "source_export",
            ModelSourceKind.RenderWareBsp,
            new RenderWareBspNativeSource(incompleteWorld, null));
        Assert.False(CollisionOverlayResolver.TryPopulate(
            incomplete, source, source.EntryName, ModelSourceKind.RenderWareBsp));
        Assert.Empty(incomplete.Materials);
        Assert.Empty(incomplete.Meshes);
        Assert.Empty(incomplete.Nodes);
        Assert.Empty(incomplete.Scenes);
        Assert.Empty(incomplete.NativeMetadata);
        Assert.Equal(0, incomplete.TriangleCount);
    }

    [CorpusTheory]
    [InlineData(Thps2PsxBuild, "skware.psx")]
    [InlineData(ApocalypsePsxBuild, "city_2.psx")]
    [InlineData(SpiderManDcBuild, "l1a2a_g.psx")]
    public void Parser_PsxLineageInlineCollisionIsOptIn(
        string build,
        string fileName)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = paths.FindSampleFile(build, fileName);
        Assert.SkipWhen(path == null, $"{fileName} not found in {build}");
        var source = new FileSystemAssetSource(path!);
        Assert.True(CollisionOverlayResolver.HasSupportedCompanion(
            source, source.EntryName, ModelSourceKind.Psx));

        var parser = new MeshModelParser();
        var plain = parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = Path.GetFileNameWithoutExtension(path),
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = false
        });
        var overlaid = parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = Path.GetFileNameWithoutExtension(path),
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = false,
            IncludeCollisionOverlay = true
        });

        Assert.Empty(plain.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        var metadata = Assert.Single(
            overlaid.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        Assert.Equal(metadata.TriangleCount,
            overlaid.TriangleCount - plain.TriangleCount);
        Assert.True(metadata.TriangleCount > 0);
        Assert.Contains(overlaid.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => primitive.NativeMetadata
                .OfType<PsxCollisionFlagsRenderMetadata>().Any());
    }

    [CorpusFact]
    public void Parser_Thps3BspInlineCollisionUsesOnlyTheMainWorld()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = paths.FindSampleFile(Thps3Ps2Build, "Burn.bsp");
        Assert.SkipWhen(path == null, "Burn.bsp not found");
        var source = new FileSystemAssetSource(path!);
        Assert.True(CollisionOverlayResolver.HasSupportedCompanion(
            source, source.EntryName, ModelSourceKind.RenderWareBsp));

        var parser = new MeshModelParser();
        var plain = parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = "Burn",
            SourceKind = ModelSourceKind.RenderWareBsp
        });
        var overlaid = parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = "Burn",
            SourceKind = ModelSourceKind.RenderWareBsp,
            IncludeCollisionOverlay = true
        });

        Assert.Equal(
            plain.Meshes.Count(static mesh => mesh.Name.StartsWith(
                "sky__", StringComparison.Ordinal)),
            overlaid.Meshes.Count(static mesh => mesh.Name.StartsWith(
                "sky__", StringComparison.Ordinal)));
        var metadata = Assert.Single(
            overlaid.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        Assert.Equal(metadata.TriangleCount,
            overlaid.TriangleCount - plain.TriangleCount);
        Assert.True(metadata.TriangleCount > 0);
        var collision = Assert.Single(overlaid.Meshes,
            static mesh => mesh.Name == "collision_overlay");
        Assert.All(collision.Primitives, primitive =>
        {
            Assert.Single(primitive.NativeMetadata
                .OfType<RwBspCollisionFlagsRenderMetadata>());
            Assert.Empty(primitive.NativeMetadata.OfType<PsxSkyRenderMetadata>());
        });
    }

    private static PsxMeshFile CreatePsxLevel()
    {
        return new PsxMeshFile
        {
            Version = 0x06,
            Objects =
            [
                new PsxMeshObject { MeshIndex = 0 }
            ],
            Meshes =
            [
                new PsxMesh
                {
                    Vertices =
                    [
                        new PsxVertex { X = 0f, Y = 0f, Z = 0f },
                        new PsxVertex { X = 1f, Y = 0f, Z = 0f },
                        new PsxVertex { X = 0f, Y = 1f, Z = 0f }
                    ],
                    Normals = [],
                    Faces =
                    [
                        new PsxFace
                        {
                            Flags = 0x1823,
                            CollisionFlags = 0x0510,
                            Index0 = 0,
                            Index1 = 1,
                            Index2 = 2
                        }
                    ],
                    InvisibleFaces = [],
                    FaceReadInfos =
                    [
                        new PsxFaceReadInfo
                        {
                            RawFaceIndex = 0,
                            Offset = 0,
                            Flags = 0x1823,
                            Length = 20,
                            BytesConsumed = 20,
                            UnderreadBytes = 0,
                            OverreadBytes = 0,
                            IsLengthAligned = true,
                            IsAccepted = true,
                            AcceptedFaceIndex = 0
                        }
                    ]
                }
            ],
            MeshNameHashes = [0],
            TextureHashes = [],
            ScaleDivisor = 1f,
            TranslationDivisor = 1f
        };
    }

    private static RwBspWorld CreateRwWorld(ushort[] flags)
    {
        return new RwBspWorld
        {
            FormatFlags = 0,
            TotalTriangles = 1,
            TotalVertices = 3,
            Materials = [],
            Sections =
            [
                new RwBspSection
                {
                    MatListWindowBase = 0,
                    Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                    Normals = null,
                    Colors = null,
                    UVs = null,
                    Triangles = [new RwTriangle(0, 1, 2, 0)],
                    TriangleCollisionFlags = flags
                }
            ]
        };
    }

    private static byte[] BuildV21SpoolEnvironmentTrg(string environmentStem)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(0x4752545Fu);
        writer.Write(0x00010002u);
        writer.Write(1u);
        writer.Write(16u);
        writer.Write((ushort)4);
        writer.Write((ushort)0x80);
        writer.Write(Encoding.ASCII.GetBytes(environmentStem));
        writer.Write((byte)0);
        if ((stream.Position & 1) != 0)
            writer.Write((byte)0);
        writer.Write(ushort.MaxValue);
        return stream.ToArray();
    }

    private sealed class StubAssetSource(
        string entryName,
        params (string Name, byte[] Bytes)[] companions) : AssetSource
    {
        public override string DisplayName => entryName;
        public override string EntryName => entryName;
        public override byte[] ReadBytes() => [];
        public override bool CompanionExists(string nameWithExtension) =>
            companions.Any(item => item.Name.Equals(
                nameWithExtension, StringComparison.OrdinalIgnoreCase));
        public override byte[]? TryReadCompanion(string nameWithExtension) =>
            companions.FirstOrDefault(item => item.Name.Equals(
                nameWithExtension, StringComparison.OrdinalIgnoreCase)).Bytes;
        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            foreach (var extension in extensions)
            {
                var bytes = TryReadCompanion(stem + extension);
                if (bytes != null)
                    return bytes;
            }

            return null;
        }
    }
}
