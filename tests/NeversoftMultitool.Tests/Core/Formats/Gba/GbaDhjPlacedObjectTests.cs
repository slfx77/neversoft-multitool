using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the placed-object bank and the chunk-record fields that address it.
///     Only bytes +0x00..+0x06 of each 16-byte record are authored, so these
///     tests deliberately assert nothing about the remaining nine.
/// </summary>
public sealed class GbaDhjPlacedObjectTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
        "Tony Hawk's Downhill Jam (USA).gba");

    [CorpusFact]
    public void ReadsThePlacedObjectCensusAndTypeSpreadOfEveryCourse()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var courses = GbaDhjCourse.FindCourses(rom);
        Assert.Equal(11, courses.Count);

        var banks = courses
            .Select(course => GbaDhjCourse.ReadObjects(rom, course))
            .ToArray();

        // Every bank yields exactly the record count its header declares.
        Assert.Equal(
            courses.Select(static course => course.ObjectCount),
            banks.Select(static bank => bank.Length));
        Assert.Equal(
            [138, 165, 90, 141, 140, 109, 110, 94, 164, 160, 33],
            banks.Select(static bank => bank.Length));
        Assert.Equal(1_344, banks.Sum(static bank => bank.Length));

        Assert.Equal(
            [19, 19, 18, 18, 19, 19, 19, 18, 18, 18, 2],
            banks.Select(static bank => bank
                .Select(static placed => placed.Type)
                .Distinct()
                .Count()));

        // The ids form three tight clusters plus one per-course id in 10..19,
        // which is why a bank cannot be a per-course model table. Ids 200-202
        // sit outside the 0..54 range the sprite jump table covers, so that
        // table is not the whole story and no id is given a meaning here.
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
                30, 31, 32, 33, 34, 35, 36, 37, 50, 51, 52, 53, 54, 200, 201, 202],
            banks.SelectMany(static bank => bank)
                .Select(static placed => (int)placed.Type)
                .Distinct()
                .Order());
    }

    [Fact]
    public void DecodesTheRuntimeCoursesLeadingPlacedObjectRecordsVerbatim()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var course = GbaDhjCourse.FindCourses(rom)[0];
        var objects = GbaDhjCourse.ReadObjects(rom, course);

        Assert.Equal(138, objects.Length);
        Assert.Equal(
            [
                new GbaDhjCourse.PlacedObject(90, -1105, 13740, 200),
                new GbaDhjCourse.PlacedObject(-34, -160, 2073, 4),
                new GbaDhjCourse.PlacedObject(-954, 54, 1308, 0),
                new GbaDhjCourse.PlacedObject(-79, -793, 10729, 3),
                new GbaDhjCourse.PlacedObject(-79, -891, 11069, 0),
                new GbaDhjCourse.PlacedObject(-79, -894, 12276, 3)
            ],
            objects.Take(6));
        Assert.Equal(new GbaDhjCourse.PlacedObject(22, -1106, 14322, 5), objects[^1]);
    }

    /// <summary>
    ///     The position fields are world XYZ in the vertex bank's own space, so
    ///     an object cannot fall outside the geometry it is placed in. A wrong
    ///     field order or stride would break this immediately: the axes have very
    ///     different ranges, the runtime course spanning roughly 1,900 units of X
    ///     against 19,000 of Z.
    /// </summary>
    [CorpusFact]
    public void EveryPlacedObjectLiesInsideItsOwnCoursesVertexBoundingBox()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var courses = GbaDhjCourse.FindCourses(rom);

        var checkedObjects = 0;
        foreach (var course in courses)
        {
            var vertices = GbaDhjCourse.ReadVertices(rom, course);
            var minX = vertices.Min(static vertex => (int)vertex.X);
            var maxX = vertices.Max(static vertex => (int)vertex.X);
            var minY = vertices.Min(static vertex => (int)vertex.Y);
            var maxY = vertices.Max(static vertex => (int)vertex.Y);
            var minZ = vertices.Min(static vertex => (int)vertex.Z);
            var maxZ = vertices.Max(static vertex => (int)vertex.Z);

            foreach (var placed in GbaDhjCourse.ReadObjects(rom, course))
            {
                Assert.InRange(placed.X, minX, maxX);
                Assert.InRange(placed.Y, minY, maxY);
                Assert.InRange(placed.Z, minZ, maxZ);
                checkedObjects++;
            }
        }

        Assert.Equal(1_344, checkedObjects);
    }

    /// <summary>
    ///     The bank carries no ordering field, so the chunk records at +0x20 and
    ///     +0x24 are the only route into it. Every stored value is either the -1
    ///     "none" slot or addresses a record of the referencing course's own
    ///     bank; the reader does not enforce that, so this measures it.
    /// </summary>
    [CorpusFact]
    public void EveryChunkObjectIndexAddressesItsOwnCoursesObjectBank()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var courses = GbaDhjCourse.FindCourses(rom);

        var used = 0;
        foreach (var course in courses)
        {
            var references = GbaDhjCourse.ReadChunkObjectReferences(rom, course);
            Assert.Equal(course.ChunkRecordCount, references.Length);
            Assert.Equal(Enumerable.Range(0, references.Length),
                references.Select(static reference => reference.ChunkIndex));

            foreach (var index in references.SelectMany(static reference =>
                         new[] { reference.FirstObjectIndex, reference.SecondObjectIndex }))
            {
                if (index == GbaDhjCourse.MissingObjectIndex)
                    continue;
                Assert.InRange(index, 0, course.ObjectCount - 1);
                used++;
            }
        }

        Assert.Equal(1_395, used);
    }

    [Fact]
    public void PlacedObjectReadersRejectForgedRangesAsInvalidData()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var course = GbaDhjCourse.FindCourses(rom)[0];

        Assert.Throws<InvalidDataException>(() => GbaDhjCourse.ReadObjects(
            rom, course with { ObjectDataOffset = int.MaxValue }));
        Assert.Throws<InvalidDataException>(() => GbaDhjCourse.ReadObjects(
            rom, course with { ObjectCount = int.MaxValue }));
        Assert.Throws<InvalidDataException>(() => GbaDhjCourse.ReadChunkObjectReferences(
            rom, course with { HeaderOffset = int.MaxValue, ChunkCount = int.MaxValue }));
    }
}
