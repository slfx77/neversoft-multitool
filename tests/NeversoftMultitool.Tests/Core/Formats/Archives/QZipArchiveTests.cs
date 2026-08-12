using System.IO.Compression;
using System.Text;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class QZipArchiveTests(TestPaths paths)
{
    private const string ThawPcBuild = "Tony Hawk's American Wasteland (2006-2-6, PC - Final)";
    private const string ThawGcBuild = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    [Fact]
    public void GetFileList_SyntheticZip_WalksStoredAndDeflateEntries()
    {
        var tempZip = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        var storedContent = Encoding.ASCII.GetBytes("stored entry payload");
        var deflateContent = Encoding.ASCII.GetBytes(new string('a', 4096)); // compressible
        try
        {
            using (var zipStream = File.Create(tempZip))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var stored = zip.CreateEntry("dir/stored.txt", CompressionLevel.NoCompression);
                using (var s = stored.Open()) s.Write(storedContent);
                var deflated = zip.CreateEntry("deflated.txt", CompressionLevel.Optimal);
                using (var s = deflated.Open()) s.Write(deflateContent);
            }

            var entries = QZipArchive.GetFileList(tempZip);
            Assert.Equal(2, entries.Count);

            var stored2 = entries.Single(e => e.Name == "stored.txt");
            Assert.Equal("dir", stored2.Directory);
            Assert.False(stored2.IsCompressed);
            Assert.Equal(storedContent.Length, stored2.Size);

            var deflated2 = entries.Single(e => e.Name == "deflated.txt");
            Assert.True(deflated2.IsCompressed);
            Assert.Equal(deflateContent.Length, deflated2.Size);
            Assert.True(deflated2.CompressedSize < deflated2.Size);

            var tempDir = tempZip + "_out";
            try
            {
                QZipArchive.ExtractFiles(tempZip, tempDir, null, TestContext.Current.CancellationToken);
                var stem = Path.GetFileNameWithoutExtension(tempZip);
                Assert.Equal(storedContent, File.ReadAllBytes(Path.Combine(tempDir, stem, "dir", "stored.txt")));
                Assert.Equal(deflateContent, File.ReadAllBytes(Path.Combine(tempDir, stem, "deflated.txt")));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }
        finally
        {
            if (File.Exists(tempZip))
                File.Delete(tempZip);
        }
    }

    [Theory]
    [InlineData("../../escaped.txt", "escaped.txt")]
    [InlineData("../malicious-other/escaped.txt", "output/malicious-other/escaped.txt")]
    public void ExtractFiles_TraversalEntryRejectsBeforeAnyPayloadIsWritten(
        string maliciousEntry,
        string escapedRelativePath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_QZipTraversal_" + Guid.NewGuid().ToString("N")[..8]);
        var zipPath = Path.Combine(tempRoot, "malicious.zip");
        var outputDir = Path.Combine(tempRoot, "output");
        var escapedPath = Path.Combine(tempRoot, escapedRelativePath);
        var safePath = Path.Combine(outputDir, "malicious", "safe.txt");
        var callbackCount = 0;
        Directory.CreateDirectory(tempRoot);

        try
        {
            using (var zipStream = File.Create(zipPath))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var safe = zip.CreateEntry("safe.txt", CompressionLevel.NoCompression);
                using (var stream = safe.Open())
                    stream.Write("safe"u8);

                var traversal = zip.CreateEntry(maliciousEntry, CompressionLevel.NoCompression);
                using (var stream = traversal.Open())
                    stream.Write("escaped"u8);
            }

            Assert.Throws<InvalidDataException>(() =>
                QZipArchive.ExtractFiles(
                    zipPath,
                    outputDir,
                    (_, _) => callbackCount++,
                    token: TestContext.Current.CancellationToken));

            Assert.Equal(0, callbackCount);
            Assert.False(Directory.Exists(outputDir));
            Assert.False(File.Exists(safePath));
            Assert.False(File.Exists(escapedPath));
            Assert.Equal([zipPath], Directory.GetFiles(tempRoot));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetContainedExportPath_AcceptsNestedRelativeAndRejectsRootedPath()
    {
        var extractionRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_Test_QZipRoot_" + Guid.NewGuid().ToString("N")[..8]));
        var expected = Path.Combine(extractionRoot, "nested", "safe.txt");
        var rootedOutside = Path.Combine(
            Path.GetPathRoot(extractionRoot)!,
            "NsMultitool_Test_QZipOutside_" + Guid.NewGuid().ToString("N"),
            "escaped.txt");

        Assert.Equal(expected,
            QZipArchive.GetContainedExportPath(extractionRoot, "nested/safe.txt"));
        Assert.Throws<InvalidDataException>(() =>
            QZipArchive.GetContainedExportPath(extractionRoot, rootedOutside));
    }

    [Fact]
    public void ExtractFiles_TraversalArchiveStemRejectsBeforeAnyPayloadIsWritten()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_QZipStem_" + Guid.NewGuid().ToString("N")[..8]);
        var zipPath = Path.Combine(tempRoot, "...zip");
        var outputDir = Path.Combine(tempRoot, "output");
        var escapedPath = Path.Combine(tempRoot, "safe.txt");
        var callbackCount = 0;
        Directory.CreateDirectory(tempRoot);

        try
        {
            using (var zipStream = File.Create(zipPath))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var safe = zip.CreateEntry("safe.txt", CompressionLevel.NoCompression);
                using var stream = safe.Open();
                stream.Write("safe"u8);
            }

            Assert.Equal("..", ArchiveNaming.GetExtractionStem(zipPath));
            Assert.Throws<InvalidDataException>(() =>
                QZipArchive.ExtractFiles(
                    zipPath,
                    outputDir,
                    (_, _) => callbackCount++,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, callbackCount);
            Assert.False(Directory.Exists(outputDir));
            Assert.False(File.Exists(escapedPath));
            Assert.Equal([zipPath], Directory.GetFiles(tempRoot));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IsZip_NonZipFile_ReturnsFalse()
    {
        var tempFile = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            File.WriteAllBytes(tempFile, [
                0x12, 0x34, 0x56, 0x78, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            ]);
            Assert.False(QZipArchive.IsZip(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetFileList_ThawPcSample_ListsArtSourcesAndDebugLog()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var zip = paths.FindSampleFiles(ThawPcBuild, "*.zip.wpc").OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.SkipWhen(zip == null, "No .zip.wpc found in THAW PC build");

        Assert.True(QZipArchive.IsZip(zip!));
        var entries = QZipArchive.GetFileList(zip!);
        Assert.True(entries.Count >= 2, "QTex zips hold at least one image + debug.log");
        Assert.Equal("debug.log", entries[^1].Name);
        Assert.All(entries, e => Assert.False(e.IsCompressed)); // QTex writes STORE only
    }

    [Fact]
    public void ExtractFiles_ThawGcSample_WritesImagePayloads()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var zip = paths.FindSampleFiles(ThawGcBuild, "*.zip.ngc").OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.SkipWhen(zip == null, "No .zip.ngc found in THAW GC build");

        var entries = QZipArchive.GetFileList(zip!);
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_QZip_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            QZipArchive.ExtractFiles(zip!, tempDir, null, TestContext.Current.CancellationToken);

            var stem = ArchiveNaming.GetExtractionStem(zip!);
            var image = entries.First(e => e.Name != "debug.log");
            var imagePath = Path.Combine(tempDir, stem, image.FullName);
            Assert.True(File.Exists(imagePath));
            Assert.Equal(image.Size, new FileInfo(imagePath).Length);

            // Payloads are little-endian TIFF (II*\0) or PNG art sources
            var magic = new byte[4];
            using (var s = File.OpenRead(imagePath)) s.ReadExactly(magic);
            var isTiff = magic is [0x49, 0x49, 0x2A, 0x00];
            var isPng = magic is [0x89, 0x50, 0x4E, 0x47];
            Assert.True(isTiff || isPng, $"Unexpected payload magic: {Convert.ToHexString(magic)}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetArchiveExtension_ZipDoubleExtensions_Collapse()
    {
        Assert.Equal(".zip", RecursiveUnpacker.GetArchiveExtension(@"C:\x\hair_f_mohawk01.tex.zip.wpc"));
        Assert.Equal(".zip", RecursiveUnpacker.GetArchiveExtension(@"C:\x\cut_iggy.tex.zip.ngc"));
        Assert.True(RecursiveUnpacker.IsArchiveFile(@"C:\x\hair_f_mohawk01.tex.zip.wpc"));
    }

    [CorpusFact]
    public void GetFileList_AllQTexZips_ParseClean()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(ThawPcBuild, "*.zip.wpc")
            .Concat(paths.FindSampleFiles(ThawGcBuild, "*.zip.ngc"))
            .ToList();
        Assert.SkipWhen(files.Count == 0, "No QTex zips found");

        var failures = new List<string>();
        var totalEntries = 0;
        foreach (var file in files)
        {
            try
            {
                var entries = QZipArchive.GetFileList(file);
                Assert.NotEmpty(entries);
                Assert.Equal("debug.log", entries[^1].Name);
                totalEntries += entries.Count;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(1337, files.Count);
        Assert.True(totalEntries > files.Count, "Every zip should hold at least one image + debug.log");
    }
}
