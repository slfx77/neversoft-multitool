using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins all eleven resource-paired course containers in the retail US ROM.
///     Course zero is also the runtime oracle retained at 0x08998BA4.
/// </summary>
public sealed class GbaDhjCourseTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
        "Tony Hawk's Downhill Jam (USA).gba");

    [Fact]
    public void FindsAllElevenCoursesBySectionAndTextureClosure()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var courses = GbaDhjCourse.FindCourses(rom);

        Assert.Equal(11, courses.Count);
        Assert.Equal(Enumerable.Range(0, 11), courses.Select(static course => course.Index));
        Assert.Equal(
            [0x998BA4, 0xA10224, 0xA95EC4, 0xB040D8, 0xB7C518, 0xBE183C,
                0xC5A838, 0xCF27F8, 0xD517B8, 0xDE523C, 0xE2BAF8],
            courses.Select(static course => course.HeaderOffset));
        Assert.Equal(
            [510, 702, 470, 440, 530, 495, 634, 440, 640, 630, 629],
            courses.Select(static course => course.ChunkCount));
        Assert.Equal(
            [11761, 14091, 8624, 10452, 7517, 4812, 13251, 4695, 13408, 7952, 10905],
            courses.Select(static course => course.VertexCount));
        Assert.Equal(
            [14219, 16764, 11642, 12304, 11089, 6952, 15568, 6794, 16629, 10645, 12236],
            courses.Select(static course => course.FaceCount));
        Assert.Equal(
            [138, 165, 90, 141, 140, 109, 110, 94, 164, 160, 33],
            courses.Select(static course => course.ObjectCount));
        Assert.Equal(
            [12, 10, 10, 12, 13, 10, 20, 16, 14, 14, 2],
            courses.Select(static course => course.TexturePageCount));
        Assert.Equal(
            [511, 681, 471, 441, 531, 496, 661, 441, 631, 631, 630],
            courses.Select(static course => course.LeftEdgePointCount));
        Assert.Equal(
            [511, 681, 471, 441, 531, 496, 635, 441, 631, 631, 0],
            courses.Select(static course => course.RightEdgePointCount));
        Assert.Equal(
            [97, 97, 44, 51, 66, 42, 56, 39, 57, 58, 13],
            courses.Select(course => GbaDhjCourse.ReadCollisionPolylines(
                rom, course).Length));

        Assert.All(courses, course =>
        {
            Assert.Equal(course.ChunkCount + 1, course.ChunkRecordCount);
            Assert.Equal(course.HeaderOffset,
                course.TextureOffset + GbaDhjCourse.TextureHeaderSize
                + course.TexturePageCount * GbaDhjCourse.TexturePageSize);
            Assert.Equal(course.LeftEdgeOffset,
                course.ObjectDataOffset + course.ObjectCount * GbaDhjCourse.ObjectRecordSize);
            Assert.Equal(course.RightEdgeOffset,
                course.LeftEdgeOffset + 2 + course.LeftEdgePointCount * 6);
        });
    }

    [Fact]
    public void DecodesRuntimeCourseMeshPaletteTexturesAndCollisionExactly()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var courses = GbaDhjCourse.FindCourses(rom);
        var course = courses[0];

        var vertices = GbaDhjCourse.ReadVertices(rom, course);
        var faces = GbaDhjCourse.ReadFaces(rom, course);
        Assert.Equal(new GbaDhjCourse.Vertex(94, 8, -765), vertices[0]);
        Assert.Equal(new GbaDhjCourse.Vertex(-872, -1113, 18871), vertices[^1]);
        Assert.Equal(new GbaDhjCourse.CentrePoint(6, -805, 1),
            GbaDhjCourse.ReadCentreLine(rom, course)[1]);
        Assert.Equal(
            new GbaDhjCourse.Face(0, 1, 2, 32280, 16152, 32280, 62591),
            faces[0]);
        Assert.Equal(
            new GbaDhjCourse.Face(11760, 11758, 11759, 32257, 16129, 32320, 57983),
            faces[^1]);
        Assert.True(faces[0].IsFlatColour);
        Assert.Equal(122, faces[0].PaletteIndex);

        var exactMesh = rom.AsSpan(
            course.VertexDataOffset,
            course.CentreLineOffset - course.VertexDataOffset);
        Assert.Equal(
            "48BE91939AD2574789CB7CE67C48D6490F1DE426BCBF8451BA9B895078F661D8",
            Convert.ToHexString(SHA256.HashData(exactMesh)));

        var pageZero = GbaDhjCourse.ReadTexturePage(rom, course, 0);
        var pageOne = GbaDhjCourse.ReadTexturePage(rom, course, 1);
        Assert.Equal((64, 64, 64 * 64 * 4),
            (pageZero.Width, pageZero.Height, pageZero.Rgba.Length));
        Assert.Equal((128, 128, 128 * 128 * 4),
            (pageOne.Width, pageOne.Height, pageOne.Rgba.Length));
        Assert.Equal(0, GbaDhjCourse.ReadPaletteRgba(rom, course)[3]);

        var polylines = GbaDhjCourse.ReadCollisionPolylines(rom, course);
        Assert.Equal(97, polylines.Length);
        Assert.Equal(0, polylines[0].PoolHalfwordOffset);
        Assert.Equal(143, polylines[0].Points.Length);
        Assert.Equal(new GbaDhjCourse.CollisionPoint(-1, -90, -984, 10029),
            polylines[0].Points[0]);
        Assert.Equal(new GbaDhjCourse.CollisionPoint(-1, -90, -985, 12882),
            polylines[0].Points[^1]);
    }

    [CorpusFact]
    public void BuildsTexturedVisualAndRenderableTriangleCollisionGlbs()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var courses = GbaDhjCourse.FindCourses(rom);
        var course = courses[0];

        var visual = GbaDhjCourseGeometryWriter.BuildVisual(rom, course, "course_00");
        Assert.Equal(14_180, visual.TriangleCount); // 39 authored zero-area records are omitted.
        Assert.Equal(82, visual.Materials.Count);
        Assert.Equal(11, visual.Textures.Count);
        var visualMesh = Assert.Single(visual.Meshes);
        Assert.Equal(82, visualMesh.Primitives.Count);
        var pageZero = Assert.Single(visualMesh.Primitives,
            static primitive => primitive.Name == "texture_page_00");
        Assert.All(pageZero.Vertices, static vertex =>
        {
            Assert.InRange(vertex.TexCoord.X, 0f, 1f);
            Assert.InRange(vertex.TexCoord.Y, 0f, 1f);
        });

        var collision = GbaDhjCourseGeometryWriter.BuildCollision(
            rom, course, "course_00_collision");
        Assert.Equal(6_826, collision.TriangleCount);
        var collisionMesh = Assert.Single(collision.Meshes);
        Assert.Equal(3, collisionMesh.Primitives.Count);
        Assert.Contains(collisionMesh.Primitives,
            static primitive => primitive.Name == "paired_road_edges_viewer_proxy");
        Assert.Contains(collisionMesh.Primitives,
            static primitive => primitive.Name == "authored_road_edges_viewer_ribbons");
        Assert.All(collisionMesh.Primitives,
            static primitive => Assert.True(primitive.Indices.Length >= 3));

        // Course 6's two edge arrays diverge in length, so their ordinal
        // connectivity is not proven. Course 10 has only one edge and a 0xCDCD
        // terminator. Both still export each exact edge and referenced line, but
        // conservatively omit the unproven filled strip between the edge arrays.
        foreach (var unpaired in new[] { courses[6], courses[10] })
        {
            var proxy = GbaDhjCourseGeometryWriter.BuildCollision(
                rom, unpaired, $"course_{unpaired.Index:D2}_collision");
            var primitives = Assert.Single(proxy.Meshes).Primitives;
            Assert.Equal(2, primitives.Count);
            Assert.Contains(primitives,
                static primitive => primitive.Name == "referenced_collision_polylines");
            Assert.Contains(primitives,
                static primitive => primitive.Name == "authored_road_edges_viewer_ribbons");
            Assert.DoesNotContain(primitives,
                static primitive => primitive.Name == "paired_road_edges_viewer_proxy");
        }

        var (visualGlb, visualTriangles) = new GltfModelExporter().BuildGlbBytes(visual);
        var (collisionGlb, collisionTriangles) = new GltfModelExporter().BuildGlbBytes(collision);
        Assert.NotNull(visualGlb);
        Assert.NotNull(collisionGlb);
        Assert.True(visualGlb.Length > 1_000_000);
        Assert.True(collisionGlb.Length > 100_000);
        Assert.Equal(visual.TriangleCount, visualTriangles);
        Assert.Equal(collision.TriangleCount, collisionTriangles);
    }

    [Fact]
    public void RejectsTruncatedLookalikesWithoutThrowing()
    {
        var fake = new byte[0x400];
        "BXSE"u8.CopyTo(fake.AsSpan(0xAC));
        // Plausible first relative offset, but no closed downstream sections.
        BitConverter.GetBytes(0x50u).CopyTo(fake, 0x100);
        Assert.Empty(GbaDhjCourse.FindCourses(fake));

        var thps2Path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)",
            "Tony Hawk's Pro Skater 2 (USA, Europe).gba");
        Assert.SkipWhen(thps2Path == null, "THPS2 GBA ROM sample not available");
        Assert.Empty(GbaDhjCourse.FindCourses(File.ReadAllBytes(thps2Path!)));
    }

    [Fact]
    public void PublicReadersRejectForgedRangesAsInvalidData()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var course = GbaDhjCourse.FindCourses(rom)[0];

        Assert.Throws<InvalidDataException>(() => GbaDhjCourse.ReadCentreLine(
            rom, course with { CentreLineOffset = int.MaxValue, CentreLinePointCount = int.MaxValue }));
        Assert.Throws<InvalidDataException>(() => GbaDhjCourse.ReadPaletteRgba(
            rom, course with { TextureOffset = int.MaxValue }));
        Assert.Throws<InvalidDataException>(() => GbaDhjCourse.ReadLeftEdge(
            rom, course with { LeftEdgeOffset = int.MaxValue, LeftEdgePointCount = int.MaxValue }));
        Assert.Throws<InvalidDataException>(() => GbaDhjCourse.ReadCollisionPolylines(
            rom, course with { HeaderOffset = int.MaxValue, ChunkCount = int.MaxValue }));
    }
}
