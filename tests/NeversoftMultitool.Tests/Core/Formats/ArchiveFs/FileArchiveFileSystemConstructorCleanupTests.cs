using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.ArchiveFs;

public sealed class FileArchiveFileSystemConstructorCleanupTests
{
    [Fact]
    public void Constructor_WhenCompanionOpenFails_ReleasesPrimaryHandle()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows file-sharing semantics are required.");

        using var temp = new TempDirectory();
        var primaryPath = Path.Combine(temp.Path, "archive.pak.ps2");
        var companionPath = Path.Combine(temp.Path, "archive.pab.ps2");
        File.WriteAllBytes(primaryPath, [0x11, 0x22, 0x33, 0x44]);
        File.WriteAllBytes(companionPath, new byte[33]);

        using var companionLock = new FileStream(
            companionPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() =>
        {
            using var archive = new FileArchiveFileSystem(
                primaryPath, ArchiveAssetType.Pak, [], companionPath);
        });

        using var primaryLock = new FileStream(
            primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Constructor_WithReadablePrimary_ReadsEntry()
    {
        using var temp = new TempDirectory();
        var primaryPath = Path.Combine(temp.Path, "archive.pak.ps2");
        byte[] payload = [0x11, 0x22, 0x33, 0x44];
        File.WriteAllBytes(primaryPath, payload);
        var entry = new ArchiveEntry
        {
            Name = "payload.bin",
            Offset = 0,
            Size = payload.Length
        };

        using var archive = new FileArchiveFileSystem(
            primaryPath, ArchiveAssetType.Pak, [entry], null);

        Assert.Equal(payload, archive.ReadEntry(entry));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-archive-fs-constructor-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leaked handle is the assertion failure under test; cleanup must not replace it.
            }
            catch (UnauthorizedAccessException)
            {
                // File locking can surface as either exception on Windows.
            }
        }
    }
}
