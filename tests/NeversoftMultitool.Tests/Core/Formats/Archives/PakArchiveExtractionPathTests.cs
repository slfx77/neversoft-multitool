using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class PakArchiveExtractionPathTests
{
    private const uint LastSentinel = 0xB524565F;
    private const uint FileType = 0xA7F505C4;
    private const int FullEntrySize = 0xC0;
    private const int SentinelSize = 0x20;

    [Fact]
    public void ExtractFiles_TraversalEntryFailsBeforeAnyOutputOrCallback()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var pakPath = Path.Combine(tempRoot, "bundle.pak");
            WritePak(pakPath,
                ("safe/first.bin", new byte[] { 0x11 }),
                ("../../escaped.bin", new byte[] { 0x22 }));
            var escapedPath = Path.Combine(tempRoot, "escaped.bin");
            File.WriteAllBytes(escapedPath, [0xCC]);
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                PakArchive.ExtractFiles(pakPath, output, (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, callbacks);
            Assert.Equal(new byte[] { 0xCC }, File.ReadAllBytes(escapedPath));
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
            var pakPath = Path.Combine(tempRoot, "...pak");
            WritePak(pakPath, ("safe/first.bin", new byte[] { 0x11 }));
            Assert.Equal("..", Path.GetFileNameWithoutExtension(pakPath));
            var output = Path.Combine(tempRoot, "output");

            Assert.Throws<InvalidDataException>(() =>
                PakArchive.ExtractFiles(pakPath, output, token: TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(output));
            Assert.False(Directory.Exists(Path.Combine(tempRoot, "safe")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Theory]
    [InlineData("folder/safe.bin:payload")]
    [InlineData("folder/NUL.bin")]
    [InlineData("folder/trail.")]
    [InlineData("folder/trail. ")]
    public void ExtractFiles_WindowsUnsafeEntryFailsBeforeAnyOutputOrCallback(string entryName)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows filename aliases are platform-specific");

        var tempRoot = CreateTempRoot();
        try
        {
            var pakPath = Path.Combine(tempRoot, "bundle.pak");
            WritePak(pakPath,
                ("safe/first.bin", new byte[] { 0x11 }),
                (entryName, new byte[] { 0x22 }));
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                PakArchive.ExtractFiles(pakPath, output, (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, callbacks);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ExtractFiles_WindowsNonDeviceNameExtractsNormally()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows device-name rules are platform-specific");

        var tempRoot = CreateTempRoot();
        try
        {
            var pakPath = Path.Combine(tempRoot, "bundle.pak");
            var payload = new byte[] { 0x44, 0x55 };
            WritePak(pakPath, ("folder/COM10.bin", payload));
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            PakArchive.ExtractFiles(pakPath, output, (_, _) => callbacks++,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, callbacks);
            Assert.Equal(payload,
                File.ReadAllBytes(Path.Combine(output, "bundle", "folder", "COM10.bin")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Theory]
    [InlineData("folder/COM¹.bin")]
    [InlineData("folder/COM²")]
    [InlineData("folder/COM³.track")]
    [InlineData("folder/LPT¹.bin")]
    [InlineData("folder/LPT²")]
    [InlineData("folder/LPT³.track")]
    public void GetContainedPath_WindowsSuperscriptDeviceNameIsRejected(string relativePath)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows device-name rules are platform-specific");

        var root = Path.Combine(Path.GetTempPath(), "nmt-pak-path-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<InvalidDataException>(() =>
            ArchiveExtractionPath.GetContainedPath(root, relativePath, "Test path"));
    }

    [Theory]
    [InlineData("folder/COM0.bin", "folder/COM0.bin")]
    [InlineData("folder/LPT10.bin", "folder/LPT10.bin")]
    [InlineData("folder/COM⁴.bin", "folder/COM⁴.bin")]
    [InlineData("foo/../bar.bin", "bar.bin")]
    public void GetContainedPath_WindowsSafeSpellingRemainsValid(string relativePath, string expectedRelativePath)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows device-name rules are platform-specific");

        var root = Path.Combine(Path.GetTempPath(), "nmt-pak-path-" + Guid.NewGuid().ToString("N"));

        var actual = ArchiveExtractionPath.GetContainedPath(root, relativePath, "Test path");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, expectedRelativePath)), actual);
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "nmt-pak-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WritePak(string path, params (string Name, byte[] Data)[] entries)
    {
        var sentinelOffset = FullEntrySize * entries.Length;
        var dataOffset = sentinelOffset + SentinelSize;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        var payloadOffset = dataOffset;
        for (var i = 0; i < entries.Length; i++)
        {
            var entryOffset = FullEntrySize * i;
            var nameBytes = Encoding.ASCII.GetBytes(entries[i].Name);
            Assert.InRange(nameBytes.Length, 1, 159);
            writer.Write(FileType);
            writer.Write((uint)(payloadOffset - entryOffset));
            writer.Write((uint)entries[i].Data.Length);
            writer.Write(new byte[16]);
            writer.Write(0x20u);
            writer.Write(nameBytes);
            writer.Write(new byte[160 - nameBytes.Length]);
            payloadOffset += entries[i].Data.Length;
        }

        Assert.Equal(sentinelOffset, writer.BaseStream.Position);
        writer.Write(LastSentinel);
        writer.Write(new byte[SentinelSize - sizeof(uint)]);
        foreach (var entry in entries)
            writer.Write(entry.Data);
    }
}
