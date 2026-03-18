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
            Assert.Contains(result, a => a.FilePath.EndsWith("game.wad"));
            Assert.Contains(result, a => a.FilePath.EndsWith("data.pre"));
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
}