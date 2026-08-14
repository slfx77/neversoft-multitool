using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class BonArchiveTests
{
    [Fact]
    public void GetFileList_V4DeclaredDdsPastEnd_ThrowsInvalidDataException()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var bonPath = Path.Combine(tempRoot, "truncated.bon");
            File.WriteAllBytes(bonPath, BuildVersion4Bon("x", 4, [0x10, 0x20, 0x30]));

            var exception = Assert.Throws<InvalidDataException>(() => BonArchive.GetFileList(bonPath));

            Assert.Contains("DDS payload", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void GetFileList_V4DeclaredDdsEndingAtEof_IsAccepted()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var bonPath = Path.Combine(tempRoot, "exact.bon");
            var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
            File.WriteAllBytes(bonPath, BuildVersion4Bon("x", 4, payload));

            var entry = Assert.Single(BonArchive.GetFileList(bonPath));

            Assert.Equal("x.DDS", entry.Name);
            Assert.Equal(4L, entry.Size);
            Assert.Equal(payload, File.ReadAllBytes(bonPath)[(int)entry.Offset..]);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void GetFileList_V1DeclaredPvrPastEnd_ThrowsInvalidDataException()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var bonPath = Path.Combine(tempRoot, "truncated-v1.bon");
            File.WriteAllBytes(bonPath, BuildVersion1Bon("x.pvr", 4, [0x10, 0x20, 0x30]));

            var exception = Assert.Throws<InvalidDataException>(() => BonArchive.GetFileList(bonPath));

            Assert.Contains("PVR payload", exception.Message, StringComparison.Ordinal);
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
            var bonPath = Path.Combine(tempRoot, "textures.bon");
            WriteVersion4Bon(bonPath,
                ("safe/first", "safe"u8.ToArray()),
                ("../../escaped", "evil"u8.ToArray()));
            var escapedPath = Path.Combine(tempRoot, "escaped.DDS");
            File.WriteAllBytes(escapedPath, "original"u8.ToArray());
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                BonArchive.ExtractFiles(bonPath, output, (_, _) => callbacks++,
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
            var bonPath = Path.Combine(tempRoot, "...bon");
            WriteVersion4Bon(bonPath, ("safe", "safe"u8.ToArray()));
            Assert.Equal("..", Path.GetFileNameWithoutExtension(bonPath));
            var output = Path.Combine(tempRoot, "output");

            Assert.Throws<InvalidDataException>(() =>
                BonArchive.ExtractFiles(bonPath, output, token: TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(Path.Combine(tempRoot, "safe.DDS")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "nmt-bon-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteVersion4Bon(string path, params (string Name, byte[] Data)[] entries)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("Bon\0"u8);
        writer.Write(4u);
        writer.Write((uint)entries.Length);

        foreach (var entry in entries)
        {
            var nameBytes = Encoding.ASCII.GetBytes(entry.Name);
            Assert.InRange(nameBytes.Length, 1, ushort.MaxValue);
            writer.Write((ushort)0);
            writer.Write(new byte[12]);
            writer.Write((byte)1);
            writer.Write((ushort)nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(new byte[3]);
            writer.Write((uint)entry.Data.Length);
            writer.Write(entry.Data);
        }
    }

    private static byte[] BuildVersion4Bon(string name, uint declaredSize, byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("Bon\0"u8);
        writer.Write(4u);
        writer.Write(1u);
        writer.Write((ushort)0); // display-name length
        writer.Write(new byte[12]); // RGBA + specular + glossiness
        writer.Write((byte)1); // has texture
        var nameBytes = Encoding.ASCII.GetBytes(name);
        writer.Write(checked((ushort)nameBytes.Length));
        writer.Write(nameBytes);
        writer.Write(new byte[3]);
        writer.Write(declaredSize);
        writer.Write(payload);
        return stream.ToArray();
    }

    private static byte[] BuildVersion1Bon(string devPath, uint declaredSize, byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("Bon\0"u8);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write((byte)0); // part-name length
        writer.Write(new byte[7 * 4]); // material floats
        writer.Write((byte)0); // flag1
        writer.Write((byte)1); // has texture
        var pathBytes = Encoding.ASCII.GetBytes(devPath);
        writer.Write(checked((byte)pathBytes.Length));
        writer.Write(pathBytes);
        writer.Write(new byte[3]);
        writer.Write(declaredSize);
        writer.Write(payload);
        return stream.ToArray();
    }
}
