using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

public sealed class GlbScratchFileTests
{
    [Fact]
    public void Write_TraversalScope_RejectsBeforeCreatingEscapedDirectory()
    {
        var escapedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"nmt-glb-scratch-escape-{Guid.NewGuid():N}");
        var scope = Path.Combine("..", Path.GetFileName(escapedDirectory));

        try
        {
            var exception = Assert.Throws<ArgumentException>(
                () => GlbScratchFile.Write([0x01], scope));

            Assert.Equal("scope", exception.ParamName);
            Assert.False(Directory.Exists(escapedDirectory));
        }
        finally
        {
            if (Directory.Exists(escapedDirectory))
                Directory.Delete(escapedDirectory, recursive: true);
        }
    }

    [Fact]
    public void Write_RootedOutsideScope_RejectsBeforeCreatingDirectory()
    {
        var escapedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"nmt-glb-scratch-rooted-{Guid.NewGuid():N}");

        try
        {
            var exception = Assert.Throws<ArgumentException>(
                () => GlbScratchFile.Write([0x02], escapedDirectory));

            Assert.Equal("scope", exception.ParamName);
            Assert.False(Directory.Exists(escapedDirectory));
        }
        finally
        {
            if (Directory.Exists(escapedDirectory))
                Directory.Delete(escapedDirectory, recursive: true);
        }
    }

    [Fact]
    public void Write_NestedContainedScope_WritesPayloadAndCanDeleteFile()
    {
        var testRootName = $"nmt-glb-scratch-valid-{Guid.NewGuid():N}";
        var testScope = Path.Combine(testRootName, "Nested");
        var testRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "NeversoftMultitool", testRootName));
        var expectedDirectory = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "NeversoftMultitool", testScope));
        byte[] payload = [0x10, 0x20, 0x30];
        string? path = null;

        try
        {
            path = GlbScratchFile.Write(payload, testScope);

            Assert.Equal(expectedDirectory, Path.GetDirectoryName(path));
            Assert.Equal(".glb", Path.GetExtension(path));
            Assert.Equal(payload, File.ReadAllBytes(path));

            GlbScratchFile.TryDelete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (path != null)
                GlbScratchFile.TryDelete(path);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }
}
