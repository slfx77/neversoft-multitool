using System.Text;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class WadArchiveTests(TestPaths paths)
{
    [Fact]
    public void GetFileList_MatchesGoldenManifest()
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var wadPath = Path.Combine(paths.WadDir!, "CD.WAD");
        Assert.SkipWhen(!File.Exists(wadPath), "WAD test file not found");
        Assert.SkipWhen(!File.Exists(paths.GoldenWadManifest!), "Golden manifest not found");

        var entries = WadArchive.GetFileList(wadPath);
        var goldenJson = File.ReadAllText(paths.GoldenWadManifest!);
        var golden = JsonDocument.Parse(goldenJson);

        var expectedCount = golden.RootElement.GetProperty("fileCount").GetInt32();
        Assert.Equal(expectedCount, entries.Count);

        var goldenEntries = golden.RootElement.GetProperty("entries");
        for (var i = 0; i < entries.Count; i++)
        {
            var expected = goldenEntries[i];
            Assert.Equal(expected.GetProperty("name").GetString(), entries[i].Name);
            Assert.Equal(expected.GetProperty("size").GetUInt32(), entries[i].Size);
            Assert.Equal(expected.GetProperty("offset").GetUInt32(), (uint)entries[i].Offset);
        }
    }

    [Fact]
    public void ExtractFiles_AllFilesExtracted()
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var wadPath = Path.Combine(paths.WadDir!, "CD.WAD");
        Assert.SkipWhen(!File.Exists(wadPath), "WAD test file not found");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Wad_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var expectedEntries = WadArchive.GetFileList(wadPath);

            var extractedCount = 0;
            WadArchive.ExtractFiles(wadPath, tempDir, (current, total) => { extractedCount = current; },
                TestContext.Current.CancellationToken);

            Assert.Equal(expectedEntries.Count, extractedCount);

            // Verify each extracted file exists and has correct size
            foreach (var entry in expectedEntries)
            {
                var extractedPath = Path.Combine(tempDir, "CD", entry.Name);
                Assert.True(File.Exists(extractedPath), $"Extracted file not found: {entry.Name}");
                var info = new FileInfo(extractedPath);
                Assert.Equal(entry.Size, (uint)info.Length);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFileList_MissingHed_ThrowsFileNotFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_NoHed");
        var tempWad = Path.Combine(tempDir, "missing.WAD");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempWad, [0x00]);

            Assert.Throws<FileNotFoundException>(() => WadArchive.GetFileList(tempWad));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFileList_OneCharacterPs1Name_IsPlaintext()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var wadPath = Path.Combine(tempRoot, "one-character.wad");
            WritePs1Wad(
                wadPath,
                ("a", [0x2A]),
                ("normal.bin", [0x10, 0x20]));

            var entries = WadArchive.GetFileList(wadPath);

            Assert.Collection(
                entries,
                entry =>
                {
                    Assert.Equal("a", entry.Name);
                    Assert.Equal(0u, entry.Crc);
                    Assert.Equal(0, entry.Offset);
                    Assert.Equal(1, entry.Size);
                },
                entry =>
                {
                    Assert.Equal("normal.bin", entry.Name);
                    Assert.Equal(0u, entry.Crc);
                    Assert.Equal(1, entry.Offset);
                    Assert.Equal(2, entry.Size);
                });
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void GetFileList_HashedLowByteAsciiAndNull_RemainsHashed()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var wadPath = Path.Combine(tempRoot, "hashed.wad");
            File.WriteAllBytes(wadPath, [0x2A]);
            using (var writer = new BinaryWriter(File.Create(WadArchive.GetHedPath(wadPath))))
            {
                writer.Write(0x00000061u);
                writer.Write(0u);
                writer.Write(1u);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
            }

            var entry = Assert.Single(WadArchive.GetFileList(wadPath));

            Assert.Equal(0x00000061u, entry.Crc);
            Assert.Equal(0, entry.Offset);
            Assert.Equal(1, entry.Size);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ExtractFiles_TraversalEntryFailsBeforeAnyOutputOrCallback()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var wadPath = Path.Combine(tempRoot, "malicious.wad");
            WritePs1Wad(wadPath,
                ("safe.txt", "safe"u8.ToArray()),
                ("../../escaped.txt", "evil"u8.ToArray()));
            var escapedPath = Path.Combine(tempRoot, "escaped.txt");
            File.WriteAllBytes(escapedPath, "original"u8.ToArray());
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                WadArchive.ExtractFiles(wadPath, output, (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, callbacks);
            Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(escapedPath));
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ExtractFiles_TraversalArchiveStemFailsBeforeAnyOutput()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var wadPath = Path.Combine(tempRoot, "...wad");
            WritePs1Wad(wadPath, ("safe.txt", "safe"u8.ToArray()));
            Assert.Equal("..", Path.GetFileNameWithoutExtension(wadPath));
            var output = Path.Combine(tempRoot, "output");

            Assert.Throws<InvalidDataException>(() =>
                WadArchive.ExtractFiles(wadPath, output, token: TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(Path.Combine(tempRoot, "safe.txt")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "nmt-wad-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WritePs1Wad(string wadPath, params (string Name, byte[] Data)[] entries)
    {
        using (var wad = File.Create(wadPath))
        {
            foreach (var entry in entries)
                wad.Write(entry.Data);
        }

        using var hed = File.Create(WadArchive.GetHedPath(wadPath));
        using var writer = new BinaryWriter(hed, Encoding.ASCII);
        uint offset = 0;
        foreach (var entry in entries)
        {
            writer.Write(Encoding.ASCII.GetBytes(entry.Name));
            writer.Write((byte)0);
            while (writer.BaseStream.Position % 4 != 0)
                writer.Write((byte)0);
            writer.Write(offset);
            writer.Write((uint)entry.Data.Length);
            offset += (uint)entry.Data.Length;
        }

        writer.Write((byte)0xFF);
    }
}
