using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

public sealed class N64RomArchiveCancellationTests
{
    [Fact]
    public void ExtractFiles_PreCancelled_DoesNotAccessInputOrCreateOutputDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_N64Rom_Cancellation_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var romPath = Path.Combine(tempDir, "missing.z64");
            var outputDir = Path.Combine(tempDir, "output");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                N64RomArchive.ExtractFiles(romPath, outputDir, token: cancellation.Token));
            Assert.False(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
