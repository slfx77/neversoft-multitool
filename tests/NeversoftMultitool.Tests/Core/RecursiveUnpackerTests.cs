using System.Buffers.Binary;
using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class RecursiveUnpackerTests
{
    [Fact]
    public void IsAlreadyExtracted_NoDirectory_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var fakePath = Path.Combine(tempDir, "SKATE4.WAD");
            File.WriteAllBytes(fakePath, [0x00]);

            Assert.False(RecursiveUnpacker.IsAlreadyExtracted(fakePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsAlreadyExtracted_EmptyDirectory_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var fakePath = Path.Combine(tempDir, "SKATE4.WAD");
            File.WriteAllBytes(fakePath, [0x00]);
            Directory.CreateDirectory(Path.Combine(tempDir, "SKATE4"));

            Assert.False(RecursiveUnpacker.IsAlreadyExtracted(fakePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsAlreadyExtracted_NonEmptyDirectory_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var fakePath = Path.Combine(tempDir, "SKATE4.WAD");
            File.WriteAllBytes(fakePath, [0x00]);
            var extractDir = Path.Combine(tempDir, "SKATE4");
            Directory.CreateDirectory(extractDir);
            File.WriteAllBytes(Path.Combine(extractDir, "file.txt"), [0x00]);

            Assert.True(RecursiveUnpacker.IsAlreadyExtracted(fakePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsArchiveFile_RecognizesAllFormats()
    {
        Assert.True(RecursiveUnpacker.IsArchiveFile("test.wad"));
        Assert.True(RecursiveUnpacker.IsArchiveFile("test.WAD"));
        Assert.True(RecursiveUnpacker.IsArchiveFile("test.pre"));
        Assert.True(RecursiveUnpacker.IsArchiveFile("test.prx"));
        Assert.True(RecursiveUnpacker.IsArchiveFile("test.pkr"));
        Assert.True(RecursiveUnpacker.IsArchiveFile("test.ddx"));
        Assert.True(RecursiveUnpacker.IsArchiveFile("test.bon"));
        Assert.False(RecursiveUnpacker.IsArchiveFile("test.psx"));
        Assert.False(RecursiveUnpacker.IsArchiveFile("test.tex"));
        Assert.False(RecursiveUnpacker.IsArchiveFile("test.glb"));
    }

    [Fact]
    public void Scan_EmptyDirectory_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var result = RecursiveUnpacker.Scan(tempDir);
            Assert.Empty(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Scan_FindsArchiveFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(Path.Combine(tempDir, "game.wad"), [0x00]);
            File.WriteAllBytes(Path.Combine(tempDir, "data.pre"), [0x00]);
            File.WriteAllBytes(Path.Combine(tempDir, "readme.txt"), [0x00]);

            var result = RecursiveUnpacker.Scan(tempDir);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, a => a.FilePath.EndsWith("game.wad", StringComparison.Ordinal));
            Assert.Contains(result, a => a.FilePath.EndsWith("data.pre", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Scan_MarksAlreadyExtracted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(Path.Combine(tempDir, "game.wad"), [0x00]);

            // Create non-empty output dir for "game"
            var extractDir = Path.Combine(tempDir, "game");
            Directory.CreateDirectory(extractDir);
            File.WriteAllBytes(Path.Combine(extractDir, "data.bin"), [0x00]);

            var result = RecursiveUnpacker.Scan(tempDir);

            Assert.Single(result);
            Assert.True(result[0].AlreadyExtracted);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Scan_FindsNestedArchives()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(Path.Combine(tempDir, "outer.wad"), [0x00]);
            var subDir = Path.Combine(tempDir, "subdir");
            Directory.CreateDirectory(subDir);
            File.WriteAllBytes(Path.Combine(subDir, "inner.pre"), [0x00]);

            var result = RecursiveUnpacker.Scan(tempDir);

            Assert.Equal(2, result.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExtractArchive_PreCancelled_DoesNotCreateOutputDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var archivePath = Path.Combine(tempDir, "missing.iso");
            var outputDir = Path.Combine(tempDir, "missing");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                RecursiveUnpacker.ExtractArchive(archivePath, cancellation.Token));
            Assert.False(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExtractArchive_InvalidGuardedZip_Throws()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var archivePath = Path.Combine(tempDir, "changed.zip");
            File.WriteAllBytes(archivePath, CreateRecognizedZipHeader());
            Assert.Equal("ZIP", RecursiveUnpacker.ClassifyArchive(archivePath));
            File.WriteAllBytes(archivePath, new byte[30]);

            var exception = Assert.Throws<InvalidDataException>(() =>
                RecursiveUnpacker.ExtractArchive(archivePath));

            Assert.Equal($"Unsupported or invalid archive: {archivePath}", exception.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExtractAll_ZipInvalidatedAfterDiscovery_RecordsErrorInsteadOfSuccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Unpack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var archivePath = Path.Combine(tempDir, "changed.zip");
            File.WriteAllBytes(archivePath, CreateRecognizedZipHeader());

            var results = RecursiveUnpacker.ExtractAll(
                tempDir,
                onPassDiscovered: (_, archives) =>
                {
                    var discovered = Assert.Single(archives);
                    Assert.Equal(archivePath, discovered.FilePath);
                    File.WriteAllBytes(archivePath, new byte[30]);
                });

            var result = Assert.Single(results);
            Assert.False(result.Extracted);
            Assert.Equal($"Unsupported or invalid archive: {archivePath}", result.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private static byte[] CreateRecognizedZipHeader()
    {
        var data = new byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x04034B50);
        return data;
    }
}
