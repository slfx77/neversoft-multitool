using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Wgt;

namespace NeversoftMultitool.Tests.Core.Formats.Wgt;

public sealed class CutsceneWeightMapFileTests(TestPaths paths)
{
    private const string ThugPs2Build = "Tony Hawk's Underground (2003-10-2, PS2 - Final)";
    private const string Thug2XboxBuild = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    private static readonly byte[] SyntheticV1 = Convert.FromHexString(
        "0100000002000000" +
        "0000803F00000000000000000000803E0000403F00000000" +
        "1D00FF1E1F00");

    private static readonly byte[] HashSeparator = [0];

    [Theory]
    [InlineData(CutsceneWeightMapPlatform.Ps2)]
    [InlineData(CutsceneWeightMapPlatform.Xbox)]
    public void Parse_Version1_PreservesRawWeightAndSignedIndexTriples(
        CutsceneWeightMapPlatform platform)
    {
        var document = CutsceneWeightMapFile.Parse(SyntheticV1, platform);

        Assert.Equal(platform, document.Platform);
        Assert.Equal(1u, document.Version);
        Assert.Equal(38, document.SerializedSize);
        Assert.Equal("E9A0A192D253D1DFA6539840BCBDC5A7D6C607C8B4D279532702ED640CCAB191",
            document.SerializedSha256);
        Assert.Equal(2, document.Vertices.Length);

        Assert.Equal([1f, 0f, 0f], document.Vertices[0].Weights);
        Assert.Equal(new sbyte[] { 29, 0, -1 }, document.Vertices[0].BoneIndices);
        Assert.Equal([0.25f, 0.75f, 0f], document.Vertices[1].Weights);
        Assert.Equal(new sbyte[] { 30, 31, 0 }, document.Vertices[1].BoneIndices);
    }

    [Theory]
    [InlineData(CutsceneWeightMapPlatform.Ps2)]
    [InlineData(CutsceneWeightMapPlatform.Xbox)]
    public void Parse_EmptyVersion1Header_PreservesExplicitPlatform(
        CutsceneWeightMapPlatform platform)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);

        var document = CutsceneWeightMapFile.Parse(data, platform);

        Assert.Equal(platform, document.Platform);
        Assert.Empty(document.Vertices);
    }

    [Fact]
    public void Parse_RejectsWrongVersionNegativeCountNonExactEofNonFiniteAndUnknownPlatform()
    {
        Assert.Throws<InvalidDataException>(() => CutsceneWeightMapFile.Parse(
            SyntheticV1.AsSpan(0, 7), CutsceneWeightMapPlatform.Ps2));

        var version2 = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(version2, 2);
        Assert.Throws<InvalidDataException>(() =>
            CutsceneWeightMapFile.Parse(version2, CutsceneWeightMapPlatform.Ps2));

        var negativeCount = (byte[])SyntheticV1.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negativeCount.AsSpan(4), -1);
        Assert.Throws<InvalidDataException>(() =>
            CutsceneWeightMapFile.Parse(negativeCount, CutsceneWeightMapPlatform.Ps2));

        Assert.Throws<InvalidDataException>(() => CutsceneWeightMapFile.Parse(
            [.. SyntheticV1, 0], CutsceneWeightMapPlatform.Ps2));
        Assert.Throws<InvalidDataException>(() => CutsceneWeightMapFile.Parse(
            SyntheticV1.AsSpan(0, SyntheticV1.Length - 1), CutsceneWeightMapPlatform.Ps2));

        var nonFinite = (byte[])SyntheticV1.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(
            nonFinite.AsSpan(8), BitConverter.SingleToInt32Bits(float.NaN));
        Assert.Throws<InvalidDataException>(() =>
            CutsceneWeightMapFile.Parse(nonFinite, CutsceneWeightMapPlatform.Xbox));

        var impossibleCount = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(impossibleCount, 1);
        BinaryPrimitives.WriteInt32LittleEndian(impossibleCount.AsSpan(4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() =>
            CutsceneWeightMapFile.Parse(impossibleCount, CutsceneWeightMapPlatform.Ps2));

        Assert.Throws<ArgumentOutOfRangeException>(() => CutsceneWeightMapFile.Parse(
            SyntheticV1, (CutsceneWeightMapPlatform)99));
    }

    [Fact]
    public void Serialize_SchemaV1PinsMetadataOnlyBoundaryAndRawTriples()
    {
        var document = CutsceneWeightMapFile.Parse(SyntheticV1, CutsceneWeightMapPlatform.Ps2);

        using var json = JsonDocument.Parse(
            CutsceneWeightMapJsonExporter.Serialize("foo.wgt.ps2", document));
        var root = json.RootElement;
        Assert.Equal("neversoft.wgt.meshScaling", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("foo.wgt.ps2", root.GetProperty("source").GetString());
        Assert.Equal("ps2", root.GetProperty("platform").GetString());
        Assert.Equal("littleEndian", root.GetProperty("byteOrder").GetString());
        Assert.Equal(38, root.GetProperty("serializedSize").GetInt32());
        Assert.Equal(document.SerializedSha256, root.GetProperty("serializedSha256").GetString());
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("vertexCount").GetInt32());
        Assert.Equal("notApplied", root.GetProperty("geometryApplicationStatus").GetString());

        var vertices = root.GetProperty("vertices");
        Assert.Equal(2, vertices.GetArrayLength());
        Assert.Equal([1f, 0f, 0f],
            vertices[0].GetProperty("weights").EnumerateArray().Select(static value => value.GetSingle()));
        Assert.Equal([29, 0, -1],
            vertices[0].GetProperty("boneIndices").EnumerateArray().Select(static value => value.GetInt32()));
    }

    [Fact]
    public void Write_InvalidDocumentDoesNotReplaceExistingOutput()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nmt-wgt-export-{Guid.NewGuid():N}.json");
        const string sentinel = "keep-existing-output";
        File.WriteAllText(output, sentinel);
        try
        {
            var invalid = new CutsceneWeightMapDocument
            {
                Platform = CutsceneWeightMapPlatform.Ps2,
                Version = 1,
                SerializedSize = SyntheticV1.Length,
                SerializedSha256 = new string('0', 64),
                Vertices = [new CutsceneWeightMapVertex(float.PositiveInfinity, 0, 0, 29, 0, 0)]
            };

            Assert.Throws<InvalidDataException>(() =>
                CutsceneWeightMapJsonExporter.Write(output, "bad.wgt.ps2", invalid));
            Assert.Equal(sentinel, File.ReadAllText(output));
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    [Fact]
    public void Parse_RepresentativePs2AndXboxFiles_MatchesPinnedRuntimeOracle()
    {
        Assert.SkipWhen(paths.SampleBuildsDir == null, "Sample builds not available");

        var ps2Path = Path.Combine(paths.SampleBuildsDir!, ThugPs2Build,
            "SKATE5", "Models", "Cutscenes", "Head_cas_female01.wgt.ps2");
        var xboxPath = Path.Combine(paths.SampleBuildsDir!, Thug2XboxBuild,
            "data", "models", "Cutscenes", "Head_cas_female01.wgt.xbx");
        Assert.SkipWhen(!File.Exists(ps2Path) || !File.Exists(xboxPath),
            "Representative WGT fixtures are not available");

        var ps2 = CutsceneWeightMapFile.Parse(
            File.ReadAllBytes(ps2Path), CutsceneWeightMapPlatform.Ps2);
        Assert.Equal(18878, ps2.SerializedSize);
        Assert.Equal("924D897B50E6891F96D48D49A0E2CFCDD594D76C87189290F1809CF047093A4F",
            ps2.SerializedSha256);
        Assert.Equal(1258, ps2.Vertices.Length);
        Assert.Equal(0.829876f, ps2.Vertices[0].Weight0, 0.000001f);
        Assert.Equal(0.170124f, ps2.Vertices[0].Weight1, 0.000001f);
        Assert.Equal(new sbyte[] { 30, 31, 0 }, ps2.Vertices[0].BoneIndices);

        var xbox = CutsceneWeightMapFile.Parse(
            File.ReadAllBytes(xboxPath), CutsceneWeightMapPlatform.Xbox);
        Assert.Equal(16553, xbox.SerializedSize);
        Assert.Equal("D0FC431EB619F5F790B5E4721253B538AB29DBDFF6CC89B16F60189EFD082FE8",
            xbox.SerializedSha256);
        Assert.Equal(1103, xbox.Vertices.Length);
        Assert.Equal([0.5f, 0.5f, 0f], xbox.Vertices[0].Weights);
        Assert.Equal(new sbyte[] { 27, 28, 0 }, xbox.Vertices[0].BoneIndices);
    }

    [CorpusFact]
    public void TypedLooseCorpus_AcceptsOnlyVersion1AndPinsAcceptedSet()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var root = paths.SampleBuildsDir!;
        var bareAuthoringFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static path => path.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(8, bareAuthoringFiles.Length);
        foreach (var path in bareAuthoringFiles)
        {
            var data = File.ReadAllBytes(path);
            Assert.True(data.Length >= 4);
            var count = BinaryPrimitives.ReadInt32LittleEndian(data);
            Assert.True(count >= 0);
            Assert.Equal(checked(4L + 24L * count), data.Length);
            Assert.Throws<InvalidDataException>(() =>
                CutsceneWeightMapFile.Parse(data, CutsceneWeightMapPlatform.Ps2));
        }

        Assert.Empty(Directory.EnumerateFiles(root, "*.wgt.ngc", SearchOption.AllDirectories));

        var files = Directory.EnumerateFiles(root, "*.wgt.ps2", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.wgt.xbx", SearchOption.AllDirectories))
            .Select(path => new WgtCorpusFile(
                path,
                Path.GetRelativePath(root, path).Replace('/', '\\'),
                path.EndsWith(".wgt.ps2", StringComparison.OrdinalIgnoreCase)
                    ? CutsceneWeightMapPlatform.Ps2
                    : CutsceneWeightMapPlatform.Xbox))
            .OrderBy(static file => file.RelativeWindowsPath, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(16, files.Length);
        using var pathContentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var concatenatedContentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var accepted = 0;
        var rejectedVersion2 = 0;
        var ps2Accepted = 0;
        var xboxAccepted = 0;
        long byteCount = 0;
        long vertexCount = 0;
        var uniquePayloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file.Path);
            CutsceneWeightMapDocument document;
            try
            {
                document = CutsceneWeightMapFile.Parse(data, file.Platform);
            }
            catch (InvalidDataException)
            {
                Assert.Equal(CutsceneWeightMapPlatform.Ps2, file.Platform);
                Assert.True(data.Length >= 8);
                Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data));
                var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                Assert.True(count >= 0);
                Assert.Equal(checked(8L + 19L * count), data.Length);
                rejectedVersion2++;
                continue;
            }

            accepted++;
            if (file.Platform == CutsceneWeightMapPlatform.Ps2)
                ps2Accepted++;
            else
                xboxAccepted++;
            byteCount += data.Length;
            vertexCount += document.Vertices.Length;
            uniquePayloads.Add(document.SerializedSha256);

            var normalizedPath = file.RelativeWindowsPath.Replace('\\', '/');
            pathContentHash.AppendData(Encoding.UTF8.GetBytes(normalizedPath));
            pathContentHash.AppendData(HashSeparator);
            pathContentHash.AppendData(SHA256.HashData(data));
            concatenatedContentHash.AppendData(data);
        }

        Assert.Equal(12, accepted);
        Assert.Equal(4, rejectedVersion2);
        Assert.Equal(4, ps2Accepted);
        Assert.Equal(8, xboxAccepted);
        Assert.Equal(8, uniquePayloads.Count);
        Assert.Equal(219126, byteCount);
        Assert.Equal(14602, vertexCount);
        Assert.Equal("718F40AC62F4873ADF8BA77612568B1BFFD987C0D83EC0DBBE56B4FCCBF177AC",
            Convert.ToHexString(pathContentHash.GetHashAndReset()));
        Assert.Equal("F08B803965E3C620BDBA34B5BDEF951960BC7586A26B8FEAF1E110BF4190B15E",
            Convert.ToHexString(concatenatedContentHash.GetHashAndReset()));
    }

    private readonly record struct WgtCorpusFile(
        string Path,
        string RelativeWindowsPath,
        CutsceneWeightMapPlatform Platform);
}
