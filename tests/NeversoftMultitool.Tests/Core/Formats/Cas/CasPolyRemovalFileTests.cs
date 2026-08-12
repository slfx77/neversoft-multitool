using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Cas;

namespace NeversoftMultitool.Tests.Core.Formats.Cas;

public sealed class CasPolyRemovalFileTests(TestPaths paths)
{
    private const string Thps4Ps2Build = "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)";
    private const string Thug2XboxBuild = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    private static readonly byte[] SyntheticPs2 =
        Convert.FromHexString("02000000000008000100000000000002A4000000");

    private static readonly byte[] SyntheticXbox =
        Convert.FromHexString("020000000000004001000000800000000E0002000D000C00");

    private static readonly byte[] HashSeparator = [0];

    [Fact]
    public void Parse_Ps2Version2_PreservesMaskAndSignedVertexReference()
    {
        var document = CasPolyRemovalFile.Parse(SyntheticPs2, CasPolyRemovalPlatform.Ps2);

        Assert.Equal(CasPolyRemovalPlatform.Ps2, document.Platform);
        Assert.Equal(2u, document.Version);
        Assert.Equal(0x00080000u, document.RemovalMask);
        Assert.Equal(20, document.SerializedSize);
        Assert.Equal("429196EE42CE5D912B7182A8E477E3E0C0C1C603E9850F92D200668E6393F898",
            document.SerializedSha256);

        var entry = Assert.IsType<CasPs2PolyRemovalEntry>(Assert.Single(document.Entries));
        Assert.Equal(0x02000000u, entry.Mask);
        Assert.Equal(164, entry.VertexReference);

        var negativeReference = (byte[])SyntheticPs2.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negativeReference.AsSpan(16), -7);
        entry = Assert.IsType<CasPs2PolyRemovalEntry>(Assert.Single(
            CasPolyRemovalFile.Parse(negativeReference, CasPolyRemovalPlatform.Ps2).Entries));
        Assert.Equal(-7, entry.VertexReference);

        var thug2Reference = (byte[])SyntheticPs2.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(thug2Reference.AsSpan(16), 62740);
        entry = Assert.IsType<CasPs2PolyRemovalEntry>(Assert.Single(
            CasPolyRemovalFile.Parse(thug2Reference, CasPolyRemovalPlatform.Ps2).Entries));
        Assert.Equal(62740, entry.VertexReference); // deliberately not capped at THUG's lookup-table size
    }

    [Theory]
    [InlineData(CasPolyRemovalPlatform.Ps2)]
    [InlineData(CasPolyRemovalPlatform.Xbox)]
    public void Parse_EmptyVersion2Header_RequiresAndPreservesExplicitDialect(CasPolyRemovalPlatform platform)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);

        var document = CasPolyRemovalFile.Parse(data, platform);

        Assert.Equal(platform, document.Platform);
        Assert.Empty(document.Entries);
    }

    [Fact]
    public void Parse_XboxVersion2_PreservesRawWordsAndDerivesRuntimeFields()
    {
        var document = CasPolyRemovalFile.Parse(SyntheticXbox, CasPolyRemovalPlatform.Xbox);

        Assert.Equal(CasPolyRemovalPlatform.Xbox, document.Platform);
        Assert.Equal(2u, document.Version);
        Assert.Equal(0x40000000u, document.RemovalMask);
        Assert.Equal(24, document.SerializedSize);
        Assert.Equal("98E11B671707B0107F97DA5DEBF31542475AFC69DF7618A918489B4A98A9E6B5",
            document.SerializedSha256);

        var entry = Assert.IsType<CasXboxPolyRemovalEntry>(Assert.Single(document.Entries));
        Assert.Equal(0x00000080u, entry.Mask);
        Assert.Equal(0x0002000Eu, entry.Data0);
        Assert.Equal(0x000C000Du, entry.Data1);
        Assert.Equal(2u, entry.MeshLoadOrder);
        Assert.Equal([14, 12, 13], entry.VertexIndices);
    }

    [Fact]
    public void Parse_RejectsWrongVersionNegativeCountWrongDialectAndNonExactEof()
    {
        Assert.Throws<InvalidDataException>(() =>
            CasPolyRemovalFile.Parse(SyntheticPs2.AsSpan(0, 11), CasPolyRemovalPlatform.Ps2));

        var wrongVersion = (byte[])SyntheticPs2.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongVersion, 3);
        Assert.Throws<InvalidDataException>(() =>
            CasPolyRemovalFile.Parse(wrongVersion, CasPolyRemovalPlatform.Ps2));

        var negativeCount = (byte[])SyntheticPs2.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negativeCount.AsSpan(8), -1);
        Assert.Throws<InvalidDataException>(() =>
            CasPolyRemovalFile.Parse(negativeCount, CasPolyRemovalPlatform.Ps2));

        Assert.Throws<InvalidDataException>(() =>
            CasPolyRemovalFile.Parse([.. SyntheticPs2, 0], CasPolyRemovalPlatform.Ps2));
        Assert.Throws<InvalidDataException>(() =>
            CasPolyRemovalFile.Parse(SyntheticPs2.AsSpan(0, SyntheticPs2.Length - 1),
                CasPolyRemovalPlatform.Ps2));
        Assert.Throws<InvalidDataException>(() =>
            CasPolyRemovalFile.Parse(SyntheticPs2, CasPolyRemovalPlatform.Xbox));
        Assert.Throws<InvalidDataException>(() =>
            CasPolyRemovalFile.Parse(SyntheticXbox, CasPolyRemovalPlatform.Ps2));

        var impossibleCount = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(impossibleCount, 2);
        BinaryPrimitives.WriteInt32LittleEndian(impossibleCount.AsSpan(8), int.MaxValue);
        Assert.Throws<InvalidDataException>(() =>
            CasPolyRemovalFile.Parse(impossibleCount, CasPolyRemovalPlatform.Xbox));
    }

    [Fact]
    public void Serialize_SchemaV1PinsMetadataOnlyBoundaryAndDialectSpecificFields()
    {
        var ps2 = CasPolyRemovalFile.Parse(SyntheticPs2, CasPolyRemovalPlatform.Ps2);
        var xbox = CasPolyRemovalFile.Parse(SyntheticXbox, CasPolyRemovalPlatform.Xbox);

        using var ps2Json = JsonDocument.Parse(CasPolyRemovalJsonExporter.Serialize("foo.cas.ps2", ps2));
        var ps2Root = ps2Json.RootElement;
        Assert.Equal("neversoft.cas.polyRemoval", ps2Root.GetProperty("schema").GetString());
        Assert.Equal(1, ps2Root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("foo.cas.ps2", ps2Root.GetProperty("source").GetString());
        Assert.Equal("ps2", ps2Root.GetProperty("platform").GetString());
        Assert.Equal("littleEndian", ps2Root.GetProperty("byteOrder").GetString());
        Assert.Equal(20, ps2Root.GetProperty("serializedSize").GetInt32());
        Assert.Equal(ps2.SerializedSha256, ps2Root.GetProperty("serializedSha256").GetString());
        Assert.Equal(2, ps2Root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("0x00080000", ps2Root.GetProperty("removalMask").GetString());
        Assert.Equal(1, ps2Root.GetProperty("entryCount").GetInt32());
        Assert.Equal("notApplied", ps2Root.GetProperty("geometryApplicationStatus").GetString());
        var ps2Entry = Assert.Single(ps2Root.GetProperty("entries").EnumerateArray());
        Assert.Equal("0x02000000", ps2Entry.GetProperty("mask").GetString());
        Assert.Equal(164, ps2Entry.GetProperty("vertexReference").GetInt32());
        Assert.False(ps2Entry.TryGetProperty("data0", out _));

        using var xboxJson = JsonDocument.Parse(CasPolyRemovalJsonExporter.Serialize("foo.cas.xbx", xbox));
        var xboxRoot = xboxJson.RootElement;
        Assert.Equal("xbox", xboxRoot.GetProperty("platform").GetString());
        Assert.Equal("notApplied", xboxRoot.GetProperty("geometryApplicationStatus").GetString());
        var xboxEntry = Assert.Single(xboxRoot.GetProperty("entries").EnumerateArray());
        Assert.Equal("0x00000080", xboxEntry.GetProperty("mask").GetString());
        Assert.Equal("0x0002000E", xboxEntry.GetProperty("data0").GetString());
        Assert.Equal("0x000C000D", xboxEntry.GetProperty("data1").GetString());
        Assert.Equal(2, xboxEntry.GetProperty("meshLoadOrder").GetInt32());
        Assert.Equal([14, 12, 13],
            xboxEntry.GetProperty("vertexIndices").EnumerateArray().Select(static value => value.GetInt32()));
        Assert.False(xboxEntry.TryGetProperty("vertexReference", out _));
    }

    [Fact]
    public void Write_IncompatibleDocumentDoesNotReplaceExistingOutput()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nmt-cas-export-{Guid.NewGuid():N}.json");
        const string sentinel = "keep-existing-output";
        File.WriteAllText(output, sentinel);
        try
        {
            var incompatible = new CasPolyRemovalDocument
            {
                Platform = CasPolyRemovalPlatform.Ps2,
                Version = 2,
                RemovalMask = 0,
                SerializedSize = SyntheticXbox.Length,
                SerializedSha256 = new string('0', 64),
                Entries = [new CasXboxPolyRemovalEntry(0, 0, 0)]
            };

            Assert.Throws<InvalidDataException>(() =>
                CasPolyRemovalJsonExporter.Write(output, "bad.cas.ps2", incompatible));
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

        var ps2Path = Path.Combine(paths.SampleBuildsDir!, Thps4Ps2Build,
            "SKATE4", "Models", "Skater_f", "extra_socks.cas.ps2");
        var xboxPath = Path.Combine(paths.SampleBuildsDir!, Thug2XboxBuild,
            "data", "models", "Cutscenes", "AnchorMan_BODY.cas.xbx");
        Assert.SkipWhen(!File.Exists(ps2Path) || !File.Exists(xboxPath),
            "Representative CAS fixtures are not available");

        var ps2 = CasPolyRemovalFile.Parse(File.ReadAllBytes(ps2Path), CasPolyRemovalPlatform.Ps2);
        Assert.Equal(2412, ps2.SerializedSize);
        Assert.Equal("AD22F92B176218C83FCE261424FBEA3B13309FC1996D54BFA2F5B236804C5828",
            ps2.SerializedSha256);
        Assert.Equal(0x00080000u, ps2.RemovalMask);
        Assert.Equal(300, ps2.Entries.Length);
        var ps2First = Assert.IsType<CasPs2PolyRemovalEntry>(ps2.Entries[0]);
        Assert.Equal(0x02000000u, ps2First.Mask);
        Assert.Equal(164, ps2First.VertexReference);

        var xbox = CasPolyRemovalFile.Parse(File.ReadAllBytes(xboxPath), CasPolyRemovalPlatform.Xbox);
        Assert.Equal(396, xbox.SerializedSize);
        Assert.Equal("EC1304E7C4888281E7AE3A7D11EC43147D43EF2B4527A2762E837EB42C64C05A",
            xbox.SerializedSha256);
        Assert.Equal(0u, xbox.RemovalMask);
        Assert.Equal(32, xbox.Entries.Length);
        var xboxFirst = Assert.IsType<CasXboxPolyRemovalEntry>(xbox.Entries[0]);
        Assert.Equal(0x00000080u, xboxFirst.Mask);
        Assert.Equal(0x0002000Eu, xboxFirst.Data0);
        Assert.Equal(0x000C000Du, xboxFirst.Data1);
        Assert.Equal(2u, xboxFirst.MeshLoadOrder);
        Assert.Equal([14, 12, 13], xboxFirst.VertexIndices);
    }

    [CorpusFact]
    public void TypedLooseCorpus_ParsesStrictlyAndPinsAcceptedSet()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var root = paths.SampleBuildsDir!;
        var files = Directory.EnumerateFiles(root, "*.cas.ps2", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.cas.xbx", SearchOption.AllDirectories))
            .Select(path => new CasCorpusFile(
                path,
                Path.GetRelativePath(root, path).Replace('/', '\\'),
                path.EndsWith(".cas.ps2", StringComparison.OrdinalIgnoreCase)
                    ? CasPolyRemovalPlatform.Ps2
                    : CasPolyRemovalPlatform.Xbox))
            // The retained oracle sorts Windows relative paths before normalizing separators.
            .OrderBy(static file => file.RelativeWindowsPath, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(13076, files.Length);
        using var pathContentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var concatenatedContentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var ps2FileCount = 0;
        var xboxFileCount = 0;
        long byteCount = 0;
        long ps2EntryCount = 0;
        long xboxEntryCount = 0;
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file.Path);
            var document = CasPolyRemovalFile.Parse(data, file.Platform);
            if (file.Platform == CasPolyRemovalPlatform.Ps2)
            {
                ps2FileCount++;
                ps2EntryCount += document.Entries.Length;
            }
            else
            {
                xboxFileCount++;
                xboxEntryCount += document.Entries.Length;
            }

            byteCount += data.Length;
            var normalizedPath = file.RelativeWindowsPath.Replace('\\', '/');
            pathContentHash.AppendData(Encoding.UTF8.GetBytes(normalizedPath));
            pathContentHash.AppendData(HashSeparator);
            pathContentHash.AppendData(SHA256.HashData(data));
            concatenatedContentHash.AppendData(data);
        }

        Assert.Equal(8134, ps2FileCount);
        Assert.Equal(4942, xboxFileCount);
        Assert.Equal(145803, ps2EntryCount);
        Assert.Equal(44106, xboxEntryCount);
        Assert.Equal(1852608, byteCount);
        Assert.Equal(189909, ps2EntryCount + xboxEntryCount);
        Assert.Equal("533B728E5099B292888F10EF0B10B35E92FFD4F07CF21B1EF8C9D6A998B5B7C8",
            Convert.ToHexString(pathContentHash.GetHashAndReset()));
        Assert.Equal("3FCDE1FB65DF4C1F0DC303F405767EC64281F3F5A1FF50EF673D5094DC04D019",
            Convert.ToHexString(concatenatedContentHash.GetHashAndReset()));
    }

    private readonly record struct CasCorpusFile(
        string Path,
        string RelativeWindowsPath,
        CasPolyRemovalPlatform Platform);
}
