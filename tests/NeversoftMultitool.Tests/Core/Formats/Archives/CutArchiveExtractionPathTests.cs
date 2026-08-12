using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class CutArchiveExtractionPathTests
{
    private const uint ExtQb = 0x2BBEA5C3;

    [Fact]
    public void ExtractFiles_TraversalArchiveStemFailsBeforeAnyOutputOrCallback()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nmt-cut-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var cutPath = Path.Combine(tempRoot, "...cut");
            WriteSingleQbCut(cutPath);
            Assert.Equal("..", ArchiveNaming.GetExtractionStem(cutPath));
            var output = Path.Combine(tempRoot, "output");
            Directory.CreateDirectory(output);
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                CutArchive.ExtractFiles(cutPath, output, (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, callbacks);
            Assert.Empty(Directory.EnumerateFileSystemEntries(output));
            Assert.False(File.Exists(Path.Combine(tempRoot, "cutscene.qb")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }

    private static void WriteSingleQbCut(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        writer.Write(1);
        writer.Write(24);
        writer.Write(4);
        writer.Write(0u);
        writer.Write(ExtQb);
        writer.Write(0xDEADBEEFu);
    }
}
