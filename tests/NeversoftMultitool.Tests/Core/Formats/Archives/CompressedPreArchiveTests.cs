using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class CompressedPreArchiveTests(TestPaths paths)
{
    private const uint VersionV2 = 0xABCD0002;
    private const uint VersionV3 = 0xABCD0003;
    private const uint RawEntryChecksum = 0x11223344;
    private const uint CompressedEntryChecksum = 0x55667788;

    private const string Thps3PsxBuild = "Tony Hawk's Pro Skater 3 (2001-10-3, PSX - Final)";
    private const string Thps3Ps2Build = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";
    private const string Thug2XboxBuild = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";
    private const string Thug2WindowsBuild = "Tony Hawks Underground 2 (2004-10-4, Windows - Final)";

    [CorpusFact]
    public void IsCompressedPre_WithPs1PreFile_ReturnsFalse()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFile = FindPlainPreFixture();
        Assert.SkipWhen(preFile == null, $"{Thps3PsxBuild}/CD/tricksel.pre not available");
        AssertPlainPreFixture(preFile!);

        Assert.False(CompressedPreArchive.IsCompressedPre(preFile!));
    }

    /// <summary>
    ///     The plain-v1 PRE parser has no magic and is the fall-through for
    ///     anything IsCompressedPre declines, so an UNKNOWN compressed version
    ///     (a future 0xABCD0004) used to garbage-parse there - its first dword
    ///     is totalFileSize, not an entry count. It must refuse instead.
    ///     Guard added 2026-08-04 (THPS3/THPS4 PS1 corpus bring-up).
    /// </summary>
    [Fact]
    public void PlainPreParser_RefusesUnknownCompressedPreVersions()
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(32).CopyTo(bytes, 0);            // totalFileSize
        BitConverter.GetBytes(0xABCD0004u).CopyTo(bytes, 4);   // unknown version
        BitConverter.GetBytes(1).CopyTo(bytes, 8);             // "numEntries"

        Assert.False(CompressedPreArchive.IsCompressedPre(bytes));
        var exception = Assert.Throws<InvalidDataException>(() => PreArchive.GetFileList(bytes));
        Assert.Contains("0xABCD0004", exception.Message);
    }

    [Theory]
    [InlineData(0x6F663273L, false)]
    [InlineData(0xABCD0002L, true)]
    [InlineData(0xABCD0003L, true)]
    public void IsCompressedPre_GeneratedHeader_RecognizesOnlySupportedVersions(long versionValue, bool expected)
    {
        var version = checked((uint)versionValue);
        var data = BuildDetectionHeader(version);
        var tempRoot = CreateTempRoot();
        try
        {
            var path = Path.Combine(tempRoot, "header.pre");
            File.WriteAllBytes(path, data);

            Assert.Equal(expected, CompressedPreArchive.IsCompressedPre(data));
            Assert.Equal(expected, CompressedPreArchive.IsCompressedPre(path));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [CorpusFact]
    public void IsCompressedPre_WithV2PreFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFile = FindCompressedPreFixture();
        Assert.SkipWhen(preFile == null, $"{Thps3Ps2Build}/SKATE3/pre/Ware.pre not available");
        AssertCompressedPreFixture(preFile!);

        Assert.True(CompressedPreArchive.IsCompressedPre(preFile!));
    }

    [Theory]
    [InlineData(0xABCD0002L, 0L, 0L)]
    [InlineData(0xABCD0003L, 0x11223344L, 0x55667788L)]
    public void GetFileList_GeneratedArchive_ListsRawAndCompressedEntries(
        long versionValue, long expectedRawChecksum, long expectedCompressedChecksum)
    {
        var data = BuildSyntheticPre(checked((uint)versionValue));

        var entries = CompressedPreArchive.GetFileList(data);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal("folder/raw.bin", entry.FullName);
                Assert.Equal(3L, entry.Size);
                Assert.False(entry.IsCompressed);
                Assert.Equal(0L, entry.CompressedSize);
                Assert.Equal(checked((uint)expectedRawChecksum), entry.Crc);
                Assert.Equal(new byte[] { 0x10, 0x20, 0x30 },
                    data[(int)entry.Offset..(int)(entry.Offset + entry.Size)]);
            },
            entry =>
            {
                Assert.Equal("packed.bin", entry.FullName);
                Assert.Equal(8L, entry.Size);
                Assert.True(entry.IsCompressed);
                Assert.Equal(9L, entry.CompressedSize);
                Assert.Equal(checked((uint)expectedCompressedChecksum), entry.Crc);
                Assert.Equal(new byte[] { 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0x21, 0x21 },
                    data[(int)entry.Offset..(int)(entry.Offset + entry.CompressedSize)]);
            });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void GetFileList_TruncatedHeader_ThrowsInvalidDataException(int length)
    {
        Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(new byte[length]));
    }

    [Fact]
    public void GetFileList_NegativeEntryCount_ThrowsInvalidDataBeforeAllocation()
    {
        var data = BuildCompressedPreHeader(VersionV2, -1);

        Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_ImpossibleEntryCount_ThrowsInvalidDataBeforeAllocation()
    {
        var data = BuildCompressedPreHeader(VersionV2, 1);

        Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(data));
    }

    [Theory]
    [InlineData(12, "data size")]
    [InlineData(16, "compressed size")]
    [InlineData(20, "name size")]
    public void GetFileList_NegativeEntryField_ThrowsInvalidDataException(int fieldOffset, string fieldName)
    {
        var data = BuildVersion2SingleEntry();
        if (fieldOffset == 20)
            BitConverter.TryWriteBytes(data.AsSpan(fieldOffset), (short)-1);
        else
            BitConverter.TryWriteBytes(data.AsSpan(fieldOffset), -1);

        var exception = Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(data));
        Assert.Contains(fieldName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFileList_TruncatedDeclaredName_ThrowsInvalidDataException()
    {
        var data = BuildVersion2SingleEntry(nameSize: 4, trailingBytes: "ab"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(data));
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(8, 4)]
    public void GetFileList_TruncatedStoredPayload_ThrowsInvalidDataException(
        int dataSize, int compressedDataSize)
    {
        var data = BuildVersion2SingleEntry(
            dataSize, compressedDataSize, trailingBytes: [0x10, 0x20, 0x30]);

        Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(data));
    }

    [Theory]
    [InlineData(int.MaxValue, 0)]
    [InlineData(0, int.MaxValue)]
    public void GetFileList_IntMaxStoredPayload_ThrowsInvalidDataWithoutOffsetOverflow(
        int dataSize, int compressedDataSize)
    {
        var data = BuildVersion2SingleEntry(dataSize, compressedDataSize);

        Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_ZeroSizesAndNamesWithoutNullTerminators_RemainAccepted()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(int.MinValue); // totalFileSize is deliberately ignored.
        writer.Write(VersionV2);
        writer.Write(2);
        WriteVersion2EntryHeader(writer, 0, 0, 0);
        WriteVersion2EntryHeader(writer, 0, 0, 3);
        writer.Write("raw"u8);

        var entries = CompressedPreArchive.GetFileList(stream.ToArray());

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal("", entry.Name);
                Assert.Equal(0L, entry.Size);
                Assert.False(entry.IsCompressed);
            },
            entry =>
            {
                Assert.Equal("raw", entry.Name);
                Assert.Equal(0L, entry.Size);
                Assert.False(entry.IsCompressed);
            });
    }

    [CorpusFact]
    public void GetFileList_Ps2Pre_ReturnsNonEmptyList()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFile = FindCompressedPreFixture();
        Assert.SkipWhen(preFile == null, $"{Thps3Ps2Build}/SKATE3/pre/Ware.pre not available");
        AssertCompressedPreFixture(preFile!);

        var entries = CompressedPreArchive.GetFileList(preFile!);
        Assert.Equal(33, entries.Count);

        // All entries should have non-empty names and positive sizes
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name), "Entry has empty name");
            Assert.True(entry.Size > 0, $"Entry '{entry.FullName}' has zero size");
        }
    }

    [Theory]
    [InlineData(".pre", "synthetic", 0xABCD0002L)]
    [InlineData(".prd", "synthetic.prd", 0xABCD0003L)]
    public void ExtractFiles_GeneratedArchive_WritesRawAndCompressedPayloadsToExpectedDirectory(
        string extension, string expectedDirectoryName, long versionValue)
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var prePath = Path.Combine(tempRoot, "synthetic" + extension);
            File.WriteAllBytes(prePath, BuildSyntheticPre(checked((uint)versionValue)));
            var output = Path.Combine(tempRoot, "output");
            var progress = new List<(int Current, int Total)>();

            CompressedPreArchive.ExtractFiles(prePath, output,
                (current, total) => progress.Add((current, total)),
                TestContext.Current.CancellationToken);

            Assert.Equal(new[] { (1, 2), (2, 2) }, progress);
            var extractionRoot = Path.Combine(output, expectedDirectoryName);
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30 },
                File.ReadAllBytes(Path.Combine(extractionRoot, "folder", "raw.bin")));
            Assert.Equal("Hello!!!"u8.ToArray(),
                File.ReadAllBytes(Path.Combine(extractionRoot, "packed.bin")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [CorpusFact]
    public void ExtractFiles_Ps2Pre_AllFilesExtracted()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFile = FindCompressedPreFixture();
        Assert.SkipWhen(preFile == null, $"{Thps3Ps2Build}/SKATE3/pre/Ware.pre not available");
        AssertCompressedPreFixture(preFile!);

        var entries = CompressedPreArchive.GetFileList(preFile!);
        Assert.Equal(33, entries.Count);
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_PreV2_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var extractedCount = 0;
            CompressedPreArchive.ExtractFiles(preFile!, tempDir, (current, total) => { extractedCount = current; },
                TestContext.Current.CancellationToken);

            Assert.Equal(entries.Count, extractedCount);

            // Verify each extracted file exists and has correct decompressed size
            var archiveName = Path.GetFileNameWithoutExtension(preFile!);
            foreach (var entry in entries)
            {
                var extractedPath = Path.Combine(tempDir, archiveName, entry.FullName);
                Assert.True(File.Exists(extractedPath), $"Extracted file not found: {entry.FullName}");

                var info = new FileInfo(extractedPath);
                Assert.Equal(entry.Size, info.Length);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFileList_InvalidVersion_ThrowsInvalidData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_invalid.pre");
        try
        {
            // Write a file with wrong version
            using (var stream = File.Create(tempFile))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(100); // totalFileSize
                writer.Write(0x12345678); // wrong version
                writer.Write(0); // numEntries
            }

            Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [CorpusFact]
    public void GetFileList_AllPs2PreFiles_ParseWithoutErrors()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFiles = FindAllPs2PreFiles();
        Assert.SkipWhen(preFiles.Count == 0, "No PS2 PRE/PRX files found");

        var totalEntries = 0;
        foreach (var preFile in preFiles)
        {
            var entries = CompressedPreArchive.GetFileList(preFile);
            Assert.NotNull(entries);
            totalEntries += entries.Count;
        }

        Assert.Equal(506, preFiles.Count);
        Assert.Equal(50_190, totalEntries);
    }

    [CorpusFact]
    public void LocalizedPre_Prd_ParsesAndExtractsToFullNameDir()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var prdFile = paths.FindSampleFiles(Thug2XboxBuild, "*.prd").OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.SkipWhen(prdFile == null, "No .prd file found in THUG2 Xbox build");

        Assert.True(CompressedPreArchive.IsCompressedPre(prdFile!));
        var entries = CompressedPreArchive.GetFileList(prdFile!);
        Assert.NotEmpty(entries);

        // Localized variants must extract to a full-name dir (anims.prd/) so they
        // don't merge with the same-stem .pre/.prx siblings.
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Prd_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            CompressedPreArchive.ExtractFiles(prdFile!, tempDir, null, TestContext.Current.CancellationToken);

            var expectedDir = Path.Combine(tempDir, Path.GetFileName(prdFile!));
            Assert.True(Directory.Exists(expectedDir), $"Expected extraction dir {Path.GetFileName(prdFile!)}/");
            var extractedPath = Path.Combine(expectedDir, entries[0].FullName);
            Assert.True(File.Exists(extractedPath), $"Extracted file not found: {entries[0].FullName}");
            Assert.Equal(entries[0].Size, new FileInfo(extractedPath).Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [CorpusFact]
    public void GetFileList_AllPrdPrfFiles_ParseClean()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = new[] { Thug2XboxBuild, Thug2WindowsBuild }
            .SelectMany(build => paths.FindSampleFiles(build, "*.prd")
                .Concat(paths.FindSampleFiles(build, "*.prf")))
            .ToList();
        Assert.SkipWhen(files.Count == 0, "No .prd/.prf files found");

        var failures = new List<string>();
        var totalEntries = 0;
        foreach (var file in files)
        {
            try
            {
                Assert.True(CompressedPreArchive.IsCompressedPre(file), "not PRE v2/v3");
                var entries = CompressedPreArchive.GetFileList(file);
                Assert.NotEmpty(entries);
                totalEntries += entries.Count;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(316, files.Count);
        Assert.True(totalEntries > 0);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "nmt-compressed-pre-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] BuildDetectionHeader(uint version)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(12);
        writer.Write(version);
        writer.Write(0);
        return stream.ToArray();
    }

    private static byte[] BuildCompressedPreHeader(uint version, int entryCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0); // totalFileSize is advisory and deliberately ignored by the parser.
        writer.Write(version);
        writer.Write(entryCount);
        return stream.ToArray();
    }

    private static byte[] BuildVersion2SingleEntry(
        int dataSize = 0, int compressedDataSize = 0, short nameSize = 0, byte[]? trailingBytes = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0);
        writer.Write(VersionV2);
        writer.Write(1);
        WriteVersion2EntryHeader(writer, dataSize, compressedDataSize, nameSize);
        writer.Write(trailingBytes ?? []);
        return stream.ToArray();
    }

    private static void WriteVersion2EntryHeader(
        BinaryWriter writer, int dataSize, int compressedDataSize, short nameSize)
    {
        writer.Write(dataSize);
        writer.Write(compressedDataSize);
        writer.Write(nameSize);
        writer.Write((short)0);
    }

    private static byte[] BuildSyntheticPre(uint version)
    {
        if (version is not VersionV2 and not VersionV3)
            throw new ArgumentOutOfRangeException(nameof(version));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(0); // Patched with the completed archive size below.
        writer.Write(version);
        writer.Write(2);

        WriteSyntheticEntry(writer, version, "folder\\raw.bin", [0x10, 0x20, 0x30], null, RawEntryChecksum);
        WriteSyntheticEntry(writer, version, "packed.bin", "Hello!!!"u8.ToArray(),
            [0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0x21, 0x21], CompressedEntryChecksum);

        writer.Flush();
        var totalFileSize = checked((int)stream.Length);
        stream.Position = 0;
        writer.Write(totalFileSize);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteSyntheticEntry(BinaryWriter writer, uint version, string name,
        byte[] decodedData, byte[]? compressedData, uint checksum)
    {
        var storedData = compressedData ?? decodedData;
        var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
        var nameFieldSize = checked((short)((nameBytes.Length + 3) & ~3));

        writer.Write(decodedData.Length);
        writer.Write(compressedData?.Length ?? 0);
        writer.Write(nameFieldSize);
        writer.Write((short)0);
        if (version == VersionV3)
            writer.Write(checksum);

        writer.Write(nameBytes);
        writer.Write(new byte[nameFieldSize - nameBytes.Length]);
        writer.Write(storedData);
        writer.Write(new byte[((storedData.Length + 3) & ~3) - storedData.Length]);
    }

    private string? FindPlainPreFixture()
    {
        return paths.FindSampleFile(Thps3PsxBuild, "tricksel.pre");
    }

    private string? FindCompressedPreFixture()
    {
        return paths.FindSampleFile(Thps3Ps2Build, "Ware.pre");
    }

    private void AssertPlainPreFixture(string preFile)
    {
        AssertFixturePath(preFile, Thps3PsxBuild, "CD", "tricksel.pre");
        Assert.Equal(297_412L, new FileInfo(preFile).Length);

        using var stream = File.OpenRead(preFile);
        using var reader = new BinaryReader(stream);
        Assert.Equal(20u, reader.ReadUInt32());
        Assert.Equal(0x6F663273u, reader.ReadUInt32());
    }

    private void AssertCompressedPreFixture(string preFile)
    {
        AssertFixturePath(preFile, Thps3Ps2Build, "SKATE3", "pre", "Ware.pre");
        Assert.Equal(580_612L, new FileInfo(preFile).Length);

        using var stream = File.OpenRead(preFile);
        using var reader = new BinaryReader(stream);
        Assert.Equal(580_612, reader.ReadInt32());
        Assert.Equal(0xABCD0002u, reader.ReadUInt32());
        Assert.Equal(33, reader.ReadInt32());
    }

    private void AssertFixturePath(string actualPath, string buildName, params string[] relativeParts)
    {
        var expectedPath = Path.GetFullPath(
            Path.Combine([paths.SampleBuildsDir!, buildName, .. relativeParts]));
        Assert.True(
            string.Equals(expectedPath, Path.GetFullPath(actualPath), StringComparison.OrdinalIgnoreCase),
            $"Expected fixture '{expectedPath}', got '{actualPath}'");
    }

    private List<string> FindAllPs2PreFiles()
    {
        if (paths.SampleBuildsDir == null) return [];

        return Directory.EnumerateDirectories(paths.SampleBuildsDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(static build => build.Contains("PS2", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static build => build, StringComparer.Ordinal)
            .SelectMany(build => paths.FindSampleFiles(build, "*.pre")
                .Concat(paths.FindSampleFiles(build, "*.prx")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(CompressedPreArchive.IsCompressedPre)
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToList();
    }
}
