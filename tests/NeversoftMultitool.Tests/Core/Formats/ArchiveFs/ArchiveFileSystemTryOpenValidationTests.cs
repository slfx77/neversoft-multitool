using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.ArchiveFs;

public sealed class ArchiveFileSystemTryOpenValidationTests
{
    [Fact]
    public void TryOpen_CompressedPreWithNegativeEntryCount_ReturnsNull()
    {
        var data = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(data, data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0xABCD0003u);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), -1);

        using var archive = ArchiveFileSystem.TryOpen(data, "broken.prx", "memory::broken.prx");

        Assert.Null(archive);
    }

    [Fact]
    public void TryOpenNested_StoredSizeOverflow_ReturnsNull()
    {
        using var parent = ArchiveFileSystem.TryOpen(
            BuildCompressedPre(),
            "parent.prx",
            "memory::parent.prx");
        Assert.NotNull(parent);

        var malformedChild = new ArchiveEntry
        {
            Name = "child.pre",
            Size = 1,
            Offset = 0,
            IsCompressed = true,
            CompressedSize = long.MaxValue
        };

        var child = parent.TryOpenNested(malformedChild);

        Assert.Null(child);
    }

    private static byte[] BuildCompressedPre()
    {
        var name = Encoding.ASCII.GetBytes("payload.bin\0");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0); // total size placeholder
        writer.Write(0xABCD0003u);
        writer.Write(1);
        writer.Write(1); // decoded size
        writer.Write(0); // stored uncompressed
        writer.Write((short)name.Length);
        writer.Write((short)0);
        writer.Write(0u); // checksum
        writer.Write(name);
        writer.Write((byte)0x5A);
        while (stream.Length % 4 != 0)
            writer.Write((byte)0);

        var data = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(data, data.Length);
        return data;
    }
}
