using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2MdlPreambleTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    public static TheoryData<string, string, int, string> CanonicalObjectMdlCases => new()
    {
        {
            "z_bh", "0000C070.mdl", 38_240,
            "828BB1CC14C11041DF8872A98FDB0FE0A034931E26B51278500A08B6BE2163B7"
        },
        {
            "z_bh", "0001DC70.mdl", 37_808,
            "BFAE771B021540B655A67641B8FCFAA1C9B11EE5FB96242BEEBF1CD31B66C604"
        },
        {
            "z_ho", "00030AA0.mdl", 52_544,
            "3B9DF16C9FB2ACD7DA85082C6A7311A88AB11FCB810AED387813F524E36F463F"
        }
    };

    [Theory]
    [MemberData(nameof(CanonicalObjectMdlCases))]
    public void TryParse_CanonicalObjectMdls_DoesNotInventTheDiscardedPreEntryPrefix(
        string pakStem,
        string mdlName,
        int expectedLength,
        string expectedSha256)
    {
        var data = LoadPakMdlData(pakStem, mdlName);
        Assert.Equal(expectedLength, data.Length);
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(data)));

        // Correct header-relative extraction starts at the owned bone block. The old fixtures
        // included the preceding PAK bytes, including an unrelated CD sentinel and trailer.
        Assert.Equal(0x1F0u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(0x10u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));

        var vifStart = Ps2GeomMdlBatchScanner.FindMdlVifStart(data);
        Assert.Equal(0x20C, vifStart);

        var preamble = Ps2MdlPreamble.TryParse(data, vifStart);
        Assert.NotNull(preamble);
        Assert.Equal(vifStart, preamble!.VifStart);
        Assert.Null(preamble.SentinelStart);
        Assert.Null(preamble.SentinelEnd);
        Assert.Null(preamble.BoneSectionSize);
        Assert.Null(preamble.BoneSectionPadding);
        Assert.Empty(preamble.Bones);
        Assert.Null(preamble.Trailer);
    }

    [Fact]
    public void TryParse_WorldZoneMdl_ReturnsRawPreambleWithoutSentinel()
    {
        var data = LoadPakMdlData("z_bh", "003B2920.mdl");

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
    public void TryParse_WorldzoneMdl_RecoversPreambleRecordsWithValidQuaternions()
    {
        // The retained records begin at canonical MDL+0x8D50. The old pre-fix slice started
        // 0x2E0 bytes too early and therefore reported this same byte as MDL+0x9030.
        // Record 0 is the zone header (class_hash=0), angle ~13.2° (normalized qw ~0.993).
        var data = LoadPakMdlData("z_bh", "0001DC70.mdl");

        var vifStart = Ps2GeomMdlBatchScanner.FindMdlVifStart(data);
        var preamble = Ps2MdlPreamble.TryParse(data, vifStart);
        Assert.NotNull(preamble);
        Assert.True(preamble!.Records.Count >= 10,
            $"Expected at least 10 preamble records, found {preamble.Records.Count}");
        Assert.True(preamble.Records.ContainsKey(0x8D50),
            "Expected rec 0 at canonical MDL offset 0x8D50 (zone header)");
        Assert.True(preamble.Records.ContainsKey(0x8E40),
            "Expected rec 3 at canonical MDL offset 0x8E40");

        var rec0 = preamble.Records[0x8D50];
        Assert.Equal(0u, rec0.ClassHash);
        // Unit-quaternion qw component for ~13° rotation is ~0.993.
        Assert.InRange(rec0.Rotation.W, 0.98f, 1.00f);

        // All recovered rotations should be unit quaternions.
        foreach (var rotation in preamble.Records.Values.Select(record => record.Rotation))
        {
            var mag = MathF.Sqrt(
                rotation.X * rotation.X +
                rotation.Y * rotation.Y +
                rotation.Z * rotation.Z +
                rotation.W * rotation.W);
            Assert.InRange(mag, 0.999f, 1.001f);
        }
    }

    [Fact]
    public void ParsePakMdl_CanonicalObjectMdl_DoesNotApplySpeculativePlacement()
    {
        var scene = Ps2GeomFile.ParsePakMdl(LoadPakMdlData("z_bh", "0000C070.mdl"));

        Assert.NotNull(scene.MdlPreamble);
        Assert.Null(scene.Bones);
        Assert.Empty(scene.MdlPreamble!.Bones);
        Assert.Null(scene.MdlPreamble.SentinelStart);
        Assert.Null(scene.MdlPreamble.Trailer);

        var originCenteredDetailLeaves = scene.Leaves.Count(IsOriginCenteredDetailLeaf);
        Assert.True(originCenteredDetailLeaves >= 3,
            $"Expected >=3 detached detail leaves to remain centered at origin, found {originCenteredDetailLeaves}");
    }

    private byte[] LoadPakMdlData(string pakStem, string mdlName)
    {
        var existingExtracted = TryGetExtractedMdl(pakStem, mdlName);
        if (existingExtracted != null)
            return File.ReadAllBytes(existingExtracted);

        var pakPath = paths.FindSampleFile(BuildName, pakStem + ".pak.ps2");
        Assert.SkipWhen(pakPath is null, $"{pakStem}.pak.ps2 not found");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_MdlPreamble_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            PakArchive.ExtractFiles(pakPath!, tempDir, token: TestContext.Current.CancellationToken);

            var extractedDir = Path.Combine(tempDir, pakStem + ".pak");
            var mdlPath = Path.Combine(extractedDir, mdlName);
            Assert.True(File.Exists(mdlPath), $"MDL not found after extraction: {mdlName}");
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
