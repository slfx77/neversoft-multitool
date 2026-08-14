using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

public sealed class N64RomArchiveValidationTests
{
    [Fact]
    public void FindTables_BlockEndingExactlyAtEof_IsAccepted()
    {
        var rom = new byte[0x101E];
        WriteInt32(rom, 0x1000, 1);
        WriteInt32(rom, 0x1004, 12);
        WriteInt32(rom, 0x1008, 30);
        WriteErzMagic(rom, 0x100C);

        var table = Assert.Single(N64RomArchive.FindTables(rom));
        var block = Assert.Single(table.Blocks);

        Assert.Equal(0x100C, block.Offset);
        Assert.Equal(ErzDecoder.HeaderSize, block.Length);
    }

    [Fact]
    public void FindTables_IntMaxRelativeEndOffset_IsRejectedWithoutOverflow()
    {
        var rom = new byte[0x1020];
        WriteInt32(rom, 0x1000, 1);
        WriteInt32(rom, 0x1004, 12);
        WriteInt32(rom, 0x1008, int.MaxValue);
        WriteErzMagic(rom, 0x100C);

        var tables = N64RomArchive.FindTables(rom);

        Assert.Empty(tables);
    }

    [Fact]
    public void TryReadMasterDirectory_IntMaxRootEndOffset_IsRejected()
    {
        var rom = new byte[0x1080];

        // A valid one-block boot table anchors the master-directory pointer.
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x0FFC), 0xB0001030);
        WriteInt32(rom, 0x1000, 1);
        WriteInt32(rom, 0x1004, 12);
        WriteInt32(rom, 0x1008, 30);
        WriteErzMagic(rom, 0x100C);

        // The root's first child begins at +12, but its advertised end at
        // int.MaxValue cannot be added to the nonzero root position safely.
        WriteInt32(rom, 0x1030, 1);
        WriteInt32(rom, 0x1034, 12);
        WriteInt32(rom, 0x1038, int.MaxValue);

        // Keep that first child structurally valid so the overflowing root
        // endpoint is the only reason the master-directory probe fails.
        WriteInt32(rom, 0x103C, 0);
        WriteInt32(rom, 0x1040, 8);

        var parsed = N64RomArchive.TryReadMasterDirectory(
            rom, out _, out var groups, out _);

        Assert.False(parsed);
        Assert.Empty(groups);
    }

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }

    private static void WriteErzMagic(byte[] data, int offset)
    {
        data[offset] = (byte)'E';
        data[offset + 1] = (byte)'R';
        data[offset + 2] = (byte)'Z';
        data[offset + 3] = 1;
    }
}
