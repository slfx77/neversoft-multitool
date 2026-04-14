using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2MdlPreambleTests(TestPaths paths)
{
    public static IEnumerable<object[]> ObjectMdlCases()
    {
        yield return
        [
            "z_bh",
            "0000BED0.mdl",
            0x3AC,
            0x19C,
            0x1A0,
            0x124,
            0x1A,
            0xF44,
            new uint[]
            {
                8, 12, 23, 6, 18, 5, 20, 19, 2, 13, 9, 24, 4, 17, 1, 22, 0, 15, 7, 11, 21, 25, 14, 10, 16, 3
            }
        ];

        yield return
        [
            "z_bh",
            "0001D990.mdl",
            0x4EC,
            0x2DC,
            0x2E0,
            0x278,
            0x15,
            0xC88,
            new uint[]
            {
                10, 14, 5, 17, 8, 20, 2, 15, 11, 4, 1, 9, 0, 13, 7, 18, 19, 12, 16, 6, 3
            }
        ];

        yield return
        [
            "z_ho",
            "00030680.mdl",
            0x62C,
            0x418,
            0x420,
            0x3B4,
            0x15,
            0xCA4,
            new uint[]
            {
                17, 12, 8, 4, 16, 1, 20, 9, 13, 3, 5, 0, 11, 7, 18, 10, 19, 14, 6, 15, 2
            }
        ];
    }

    [Theory]
    [MemberData(nameof(ObjectMdlCases))]
    public void TryParse_ObjectMdls_ParsesBoneBlockAndTrailer(
        string pakStem,
        string mdlName,
        int expectedVifStart,
        int expectedSentinelStart,
        int expectedSentinelEnd,
        int expectedHeaderOffset,
        uint expectedTrailerCount,
        uint expectedRawPointer,
        uint[] expectedIndices)
    {
        var data = LoadPakMdlData(pakStem, mdlName);

        var vifStart = Ps2GeomMdlBatchScanner.FindMdlVifStart(data);
        Assert.Equal(expectedVifStart, vifStart);

        var preamble = Ps2MdlPreamble.TryParse(data, vifStart);
        Assert.NotNull(preamble);
        Assert.Equal(expectedVifStart, preamble!.VifStart);
        Assert.Equal(expectedSentinelStart, preamble.SentinelStart);
        Assert.Equal(expectedSentinelEnd, preamble.SentinelEnd);
        Assert.Equal((uint)0x1F0, preamble.BoneSectionSize);
        Assert.Equal((uint)0x10, preamble.BoneSectionPadding);
        Assert.Equal(6, preamble.Bones.Count);

        Assert.NotNull(preamble.Trailer);
        Assert.Equal(expectedHeaderOffset, preamble.Trailer!.HeaderOffset);
        Assert.Equal((uint)0, preamble.Trailer.PrefixZero);
        Assert.Equal((uint)0x00010100, preamble.Trailer.Marker);
        Assert.Equal(expectedTrailerCount, preamble.Trailer.Count);
        Assert.Equal(expectedRawPointer, preamble.Trailer.RawPointer);
        Assert.Equal(expectedIndices, preamble.Trailer.Indices.ToArray());
    }

    [Fact]
    public void TryParse_WorldZoneMdl_ReturnsRawPreambleWithoutSentinel()
    {
        var data = LoadPakMdlData("z_bh", "003B1940.mdl");

        var vifStart = Ps2GeomMdlBatchScanner.FindMdlVifStart(data);
        Assert.True(vifStart > 0, "Expected a valid VIF start");

        var preamble = Ps2MdlPreamble.TryParse(data, vifStart);
        Assert.NotNull(preamble);
        Assert.Equal(vifStart, preamble!.VifStart);
        Assert.Null(preamble.SentinelStart);
        Assert.Null(preamble.SentinelEnd);
        Assert.Null(preamble.Trailer);
        Assert.Null(preamble.BoneSectionSize);
        Assert.Null(preamble.BoneSectionPadding);
        Assert.Empty(preamble.Bones);
    }

    [Fact]
    public void ParsePakMdl_ObjectMdl_DoesNotApplySpeculativePlacement()
    {
        var scene = Ps2GeomFile.ParsePakMdl(LoadPakMdlData("z_bh", "0000BED0.mdl"));

        Assert.NotNull(scene.MdlPreamble);
        Assert.NotNull(scene.Bones);
        Assert.True(scene.Bones!.Count > 0, "Expected object MDL bones to remain available");

        var originCenteredDetailLeaves = scene.Leaves.Count(IsOriginCenteredDetailLeaf);
        Assert.True(originCenteredDetailLeaves >= 3,
            $"Expected >=3 detached detail leaves to remain centered at origin, found {originCenteredDetailLeaves}");
    }

    private string? GetThawPakDir()
    {
        if (!paths.HasSampleBuilds)
            return null;

        var dir = Path.Combine(paths.SampleBuildsDir!,
            "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "PAK");
        return Directory.Exists(dir) ? dir : null;
    }

    private byte[] LoadPakMdlData(string pakStem, string mdlName)
    {
        var existingExtracted = TryGetExtractedMdl(pakStem, mdlName);
        if (existingExtracted != null)
            return File.ReadAllBytes(existingExtracted);

        var pakDir = GetThawPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, pakStem + ".pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), $"PAK not found: {pakPath}");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_MdlPreamble_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            PakArchive.ExtractFiles(pakPath, tempDir, token: TestContext.Current.CancellationToken);

            var extractedDir = Path.Combine(tempDir, pakStem + ".pak");
            var mdlPath = Path.Combine(extractedDir, mdlName);
            Assert.SkipWhen(!File.Exists(mdlPath), $"MDL not found after extraction: {mdlName}");
            return File.ReadAllBytes(mdlPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private string? TryGetExtractedMdl(string pakStem, string mdlName)
    {
        if (paths.TestOutputDir == null)
            return null;

        var candidate = Path.Combine(paths.TestOutputDir, pakStem + "_pak", pakStem + ".pak", mdlName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool IsOriginCenteredDetailLeaf(Ps2GeomLeaf leaf)
    {
        if (leaf.Vertices.Length == 0)
            return false;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in leaf.Vertices.Select(static vertex => vertex.Position))
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var size = max - min;
        var maxDimension = Math.Max(size.X, Math.Max(size.Y, size.Z));
        var center = (min + max) * 0.5f;
        return maxDimension < 35f &&
               Math.Abs(center.X) < 5f &&
               Math.Abs(center.Y) < 5f &&
               Math.Abs(center.Z) < 5f;
    }
}
