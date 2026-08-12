using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.ArchiveFs;

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
}
