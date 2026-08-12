using NeversoftMultitool.Core.Formats.Vid1;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class ArchiveVideoTempFileTests
{
    [Theory]
    [InlineData("intro.pss")]
    [InlineData("intro.str")]
    [InlineData("intro.vid")]
    public void Write_PreservesLeafAndPayloadWhileIsolatingAndCleaningEachStage(string fileName)
    {
        byte[] payload = [0x10, 0x20, 0x30, 0x40];
        var first = ArchiveVideoTempFile.Write("ArchiveVideoTest", fileName, payload);
        ArchiveVideoTempFile? second = null;
        var firstDirectory = Path.GetDirectoryName(first.Path)!;
        var secondDirectory = string.Empty;

        try
        {
            second = ArchiveVideoTempFile.Write("ArchiveVideoTest", fileName, payload);
            secondDirectory = Path.GetDirectoryName(second.Path)!;

            Assert.Equal(fileName, Path.GetFileName(first.Path));
            Assert.Equal(Path.GetFileNameWithoutExtension(fileName),
                Path.GetFileNameWithoutExtension(first.Path));
            Assert.Equal(payload, File.ReadAllBytes(first.Path));
            Assert.Equal(payload, File.ReadAllBytes(second.Path));
            Assert.NotEqual(firstDirectory, secondDirectory);
        }
        finally
        {
            second?.Dispose();
            first.Dispose();
        }

        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
    }

    [Theory]
    [InlineData("intro.vid", Vid1VideoVariant.ThawLongForm)]
    [InlineData("atvi.vid", Vid1VideoVariant.ThawAtvi)]
    public void Write_PreservesVid1FilenameVariant(string fileName, Vid1VideoVariant expectedVariant)
    {
        using var staged = ArchiveVideoTempFile.Write(
            "ArchiveVideoVariantTest",
            fileName,
            Vid1VideoTestBuilder.CreateVideoVid1());

        var success = Vid1VideoFile.TryParse(staged.Path, out var file, out var error);

        Assert.True(success, error);
        Assert.NotNull(file);
        Assert.Equal(expectedVariant, file!.Variant);
    }

    [Fact]
    public void Write_StripsArchiveTraversalAndDisposingOneStageKeepsSiblingAlive()
    {
        byte[] firstPayload = [0x01, 0x02];
        byte[] secondPayload = [0x03, 0x04];
        var first = ArchiveVideoTempFile.Write(
            "ArchiveVideoIsolationTest",
            "../nested\\intro.vid",
            firstPayload);
        var firstDirectory = Path.GetDirectoryName(first.Path)!;
        ArchiveVideoTempFile? second = null;
        var secondDirectory = string.Empty;

        try
        {
            second = ArchiveVideoTempFile.Write(
                "ArchiveVideoIsolationTest",
                "../../other/intro.vid",
                secondPayload);
            secondDirectory = Path.GetDirectoryName(second.Path)!;

            Assert.Equal("intro.vid", Path.GetFileName(first.Path));
            Assert.Equal("intro.vid", Path.GetFileName(second.Path));
            Assert.NotEqual(firstDirectory, secondDirectory);

            first.Dispose();

            Assert.False(Directory.Exists(firstDirectory));
            Assert.True(File.Exists(second!.Path));
            Assert.Equal(secondPayload, File.ReadAllBytes(second.Path));
        }
        finally
        {
            first.Dispose();
            second?.Dispose();
        }

        Assert.False(Directory.Exists(secondDirectory));
    }

    [Theory]
    [InlineData("CON.vid")]
    [InlineData("nul.STR")]
    [InlineData("COM1.pss")]
    [InlineData("LPT9.vid")]
    [InlineData("CLOCK$.vid")]
    [InlineData("CONOUT$.vid")]
    [InlineData("NUL:.vid")]
    [InlineData("COM¹.vid")]
    [InlineData("LPT³.vid")]
    [InlineData("intro.vid. ")]
    public void Write_RejectsWindowsReservedLeavesBeforeStaging(string fileName)
    {
        Assert.Throws<ArgumentException>(() =>
            ArchiveVideoTempFile.Write("ArchiveVideoReservedTest", fileName, [0x01]));
    }
}
