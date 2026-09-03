using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class RwBspCollisionGeometryWriterTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    [Fact]
    public void PopulateOverlay_GroupsEveryUsableTriangleByExactRawFlag()
    {
        var world = CreateWorld(
            [
                new RwTriangle(0, 1, 2, 0),
                new RwTriangle(1, 3, 2, 0)
            ],
            [0x0410, 0x0080]);
        var document = new ModelDocument
        {
            Name = "test",
            SourceKind = ModelSourceKind.RenderWareBsp
        };

        var added = RwBspCollisionGeometryWriter.PopulateOverlay(document, world);

        Assert.Equal(2, added);
        Assert.Equal(2, document.TriangleCount);
        var mesh = Assert.Single(document.Meshes);
        Assert.Equal("collision_overlay", mesh.Name);
        Assert.Equal(2, mesh.Primitives.Count);
        Assert.Equal(
            new ushort[] { 0x0080, 0x0410 },
            mesh.Primitives
                .Select(static primitive => Assert.Single(
                    primitive.NativeMetadata.OfType<RwBspCollisionFlagsRenderMetadata>()).CollisionFlags)
                .ToArray());
        var material = Assert.Single(document.Materials);
        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
        Assert.True(material.DoubleSided);
        Assert.True(material.Unlit);
    }

    [Fact]
    public void PopulateOverlay_IncompleteFlagOwnershipFailsClosedWithoutMutation()
    {
        var world = CreateWorld(
            [new RwTriangle(0, 1, 2, 0)],
            []);
        var document = new ModelDocument
        {
            Name = "test",
            SourceKind = ModelSourceKind.RenderWareBsp
        };

        Assert.False(RwBspCollisionGeometryWriter.CanPopulate(world));
        Assert.Equal(0, RwBspCollisionGeometryWriter.PopulateOverlay(document, world));
        Assert.Empty(document.Materials);
        Assert.Empty(document.Meshes);
        Assert.Empty(document.Nodes);
        Assert.Empty(document.Scenes);
    }

    [Fact]
    public void PopulateOverlay_SalvagedPartialWorldFailsClosedWithoutMutation()
    {
        var world = CreateWorld(
            [new RwTriangle(0, 1, 2, 0)],
            [0x0010],
            declaredTriangleCount: 2);
        var document = new ModelDocument
        {
            Name = "test",
            SourceKind = ModelSourceKind.RenderWareBsp
        };

        Assert.False(RwBspCollisionGeometryWriter.CanPopulate(world));
        Assert.Equal(0, RwBspCollisionGeometryWriter.PopulateOverlay(document, world));
        Assert.Empty(document.Materials);
        Assert.Empty(document.Meshes);
        Assert.Empty(document.Nodes);
        Assert.Empty(document.Scenes);
    }

    [Fact]
    public void PopulateOverlay_InvalidIndexFailsClosedWithoutMutation()
    {
        var world = CreateWorld(
            [
                new RwTriangle(0, 1, 2, 0),
                new RwTriangle(0, 1, 9, 0)
            ],
            [0x0010, 0x0040]);
        var document = new ModelDocument
        {
            Name = "test",
            SourceKind = ModelSourceKind.RenderWareBsp
        };

        Assert.False(RwBspCollisionGeometryWriter.CanPopulate(world));
        Assert.Equal(0, RwBspCollisionGeometryWriter.PopulateOverlay(document, world));
        Assert.Empty(document.Materials);
        Assert.Empty(document.Meshes);
        Assert.Empty(document.Nodes);
        Assert.Empty(document.Scenes);
        Assert.Equal(0, document.TriangleCount);
    }

    [Fact]
    public void PopulateOverlay_OmitsGeometricDegenerates()
    {
        var world = CreateWorld(
            [
                new RwTriangle(0, 1, 2, 0),
                new RwTriangle(0, 0, 1, 0)
            ],
            [0x0010, 0x0020]);
        var document = new ModelDocument
        {
            Name = "test",
            SourceKind = ModelSourceKind.RenderWareBsp
        };

        Assert.True(RwBspCollisionGeometryWriter.CanPopulate(world));
        Assert.Equal(1, RwBspCollisionGeometryWriter.PopulateOverlay(document, world));
        Assert.Equal(1, document.TriangleCount);
        Assert.Single(Assert.Single(document.Meshes).Primitives);
    }

    [CorpusFact]
    public void PopulateOverlay_AllRuntimeBspCollisionPayloadsHavePinnedCoverage()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.bsp").ToArray();
        Assert.SkipWhen(files.Length == 0, "No BSP files found");

        var supportedFiles = 0;
        var sourceTriangles = 0L;
        var emittedTriangles = 0L;
        var rawFlags = new HashSet<ushort>();
        var declined = new List<string>();
        foreach (var file in files)
        {
            var world = RwBspFile.Parse(file);
            if (!RwBspCollisionGeometryWriter.CanPopulate(world))
            {
                declined.Add(Path.GetRelativePath(
                    Path.Combine(paths.SampleBuildsDir!, BuildName), file).Replace('\\', '/'));
                continue;
            }

            supportedFiles++;
            foreach (var section in world.Sections)
            {
                sourceTriangles += section.Triangles.Length;
                rawFlags.UnionWith(section.TriangleCollisionFlags);
            }

            var document = new ModelDocument
            {
                Name = Path.GetFileNameWithoutExtension(file),
                SourceKind = ModelSourceKind.RenderWareBsp
            };
            emittedTriangles += RwBspCollisionGeometryWriter.PopulateOverlay(document, world);
        }

        Assert.Equal(43, files.Length);
        Assert.Equal(39, supportedFiles);
        Assert.Equal(772_002, sourceTriangles);
        Assert.Equal(771_579, emittedTriangles);
        Assert.Equal(394, rawFlags.Count);
        // Three DCC/source exports do not carry the runtime plugin. Ware_Test10
        // is a valid but empty test BSP, so collision mode also declines it.
        Assert.Equal(
            [
                "SKATE3/Intermediate/Models/SkWare_RW.bsp",
                "SKATE3/Intermediate/Models/SkWare_RW_2.bsp",
                "SKATE3/Intermediate/Models/Ware_Test10.bsp",
                "SKATE3/Source/Ware/Ware.bsp"
            ],
            declined.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static RwBspWorld CreateWorld(
        RwTriangle[] triangles,
        ushort[] flags,
        int? declaredTriangleCount = null)
    {
        return new RwBspWorld
        {
            FormatFlags = 0,
            TotalTriangles = declaredTriangleCount ?? triangles.Length,
            TotalVertices = 4,
            Materials = [],
            Sections =
            [
                new RwBspSection
                {
                    MatListWindowBase = 0,
                    Vertices =
                    [
                        Vector3.Zero,
                        Vector3.UnitX,
                        Vector3.UnitY,
                        Vector3.One
                    ],
                    Normals = null,
                    Colors = null,
                    UVs = null,
                    Triangles = triangles,
                    TriangleCollisionFlags = flags
                }
            ]
        };
    }
}
