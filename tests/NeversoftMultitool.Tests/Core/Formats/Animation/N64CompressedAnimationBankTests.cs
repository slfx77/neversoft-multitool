using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class N64CompressedAnimationBankTests
{
    [Fact]
    public void ParseAndDecode_UsesBigEndianTableAndLittleEndianChannelValues()
    {
        var bytes = BuildShell(PsxMeshFile.HierChunkV2Tag, [(3, ConstantChannels(1, 2, 3, 4, 5, 0x1234))]);

        var bank = N64CompressedAnimationBank.TryParse(bytes);

        Assert.NotNull(bank);
        var entry = Assert.Single(bank!.Entries);
        Assert.Equal(12, entry.PoolOffset);
        Assert.Equal(3, entry.FrameCount);
        var animation = bank.DecodeSlot(0, 1);
        Assert.Equal(3, animation.FrameCount);
        Assert.Equal(1, animation.BoneCount);
        for (var frame = 0; frame < 3; frame++)
        {
            Assert.Equal(1, animation.Channels[0, 0, frame]);
            Assert.Equal(2, animation.Channels[0, 1, frame]);
            Assert.Equal(3, animation.Channels[0, 2, frame]);
            Assert.Equal(4, animation.Channels[0, 3, frame]);
            Assert.Equal(5, animation.Channels[0, 4, frame]);
            Assert.Equal(0x1234, animation.Channels[0, 5, frame]);
        }
    }

    [Fact]
    public void ParseAndDecode_DirectMatrixUsesBigEndianTableAndSMatrixCells()
    {
        var bytes = BuildDirectShell(
        [
            (2, 0, DirectMatrices(
                DirectMatrix(-12, 34, -56),
                DirectMatrix(78, -90, 123)))
        ]);

        var bank = N64CompressedAnimationBank.TryParse(bytes);

        Assert.NotNull(bank);
        Assert.Equal(PsxMeshFile.HierChunkV1Tag, bank!.ChunkTag);
        var entry = Assert.Single(bank.Entries);
        Assert.Equal(12, entry.PoolOffset);
        Assert.Equal(2, entry.FrameCount);
        Assert.Equal(0, entry.TweenFlag);
        var animation = bank.DecodeSlot(0, 1);
        Assert.Equal(2, animation.FrameCount);
        Assert.Equal(-12, animation.Channels[0, 3, 0]);
        Assert.Equal(34, animation.Channels[0, 4, 0]);
        Assert.Equal(-56, animation.Channels[0, 5, 0]);
        Assert.Equal(78, animation.Channels[0, 3, 1]);
        Assert.Equal(-90, animation.Channels[0, 4, 1]);
        Assert.Equal(123, animation.Channels[0, 5, 1]);
        Assert.All(animation.DirectRotations!.Cast<System.Numerics.Quaternion>(), rotation =>
        {
            Assert.Equal(0f, rotation.X, 5);
            Assert.Equal(0f, rotation.Y, 5);
            Assert.Equal(0f, rotation.Z, 5);
            Assert.Equal(1f, rotation.W, 5);
        });
    }

    [Fact]
    public void DecodeDirectSlot_TweenFieldExpandsStoredFrames()
    {
        var bytes = BuildDirectShell(
        [
            (5, 1, DirectMatrices(
                DirectMatrix(0, 0, 0),
                DirectMatrix(10, 0, 0),
                DirectMatrix(20, 0, 0)))
        ]);
        var bank = N64CompressedAnimationBank.TryParse(bytes);

        Assert.NotNull(bank);
        Assert.Equal(1, Assert.Single(bank!.Entries).TweenFlag);
        var animation = bank.DecodeSlot(0, 1);

        Assert.Equal(new short[] { 0, 5, 10, 15, 20 },
            Enumerable.Range(0, 5).Select(frame => animation.Channels[0, 3, frame]));
    }

    [Fact]
    public void DecodeDirectSlot_CannotBorrowLastByteFromFollowingEntry()
    {
        var truncated = DirectMatrices(
            DirectMatrix(1, 2, 3),
            DirectMatrix(4, 5, 6))[..^1];
        var bytes = BuildDirectShell(
        [
            (2, 0, truncated),
            (1, 0, DirectMatrix(7, 8, 9))
        ]);
        var bank = N64CompressedAnimationBank.TryParse(bytes);

        Assert.NotNull(bank);
        Assert.Throws<InvalidDataException>(() => bank!.DecodeSlot(0, 1));
        var second = bank!.DecodeSlot(1, 1);
        Assert.Equal(7, second.Channels[0, 3, 0]);
    }

    [Fact]
    public void DecodeDirectSlot_AllowsOneStoredFrameOfTrailingSlack()
    {
        var required = DirectMatrix(11, 22, 33);
        var payload = DirectMatrices(required, DirectMatrix(999, 999, 999));
        var bytes = BuildDirectShell([(1, 0, payload)]);
        var bank = N64CompressedAnimationBank.TryParse(bytes);

        Assert.NotNull(bank);
        var animation = bank!.DecodeSlot(0, 1);

        Assert.Equal(11, animation.Channels[0, 3, 0]);
        Assert.Equal(22, animation.Channels[0, 4, 0]);
        Assert.Equal(33, animation.Channels[0, 5, 0]);
    }

    [Fact]
    public void DecodeDirectSlot_ReportsCheckedRequiredSizeOverflow()
    {
        var bank = N64CompressedAnimationBank.TryParse(
            BuildDirectShell([(1, 0, DirectMatrix(1, 2, 3))]));

        Assert.NotNull(bank);
        var error = Assert.Throws<InvalidDataException>(() => bank!.DecodeSlot(0, int.MaxValue));
        Assert.Contains("overflowing payload size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_LastAnimationChunkWinsAcrossDirectAndCompressedVariants()
    {
        var direct = BuildDirectChunk([(1, 0, DirectMatrix(44, 55, 66))]);
        var compressed = BuildCompressedChunk([(3, ConstantChannels(1, 2, 3, 4, 5, 6))]);

        var directLast = N64CompressedAnimationBank.TryParse(BuildShellChunks(
            (PsxMeshFile.HierChunkV2Tag, compressed),
            (PsxMeshFile.HierChunkV1Tag, direct)));
        var compressedLast = N64CompressedAnimationBank.TryParse(BuildShellChunks(
            (PsxMeshFile.HierChunkV1Tag, direct),
            (PsxMeshFile.HierChunkV2Tag, compressed)));

        Assert.NotNull(directLast);
        Assert.Equal(PsxMeshFile.HierChunkV1Tag, directLast!.ChunkTag);
        Assert.Equal(44, directLast.DecodeSlot(0, 1).Channels[0, 3, 0]);
        Assert.NotNull(compressedLast);
        Assert.Equal(PsxMeshFile.HierChunkV2Tag, compressedLast!.ChunkTag);
        Assert.Equal(1, compressedLast.DecodeSlot(0, 1).Channels[0, 0, 0]);
    }

    [Fact]
    public void Parse_DirectRejectsZeroPlaybackFrames()
    {
        var bytes = BuildDirectShell([(1, 0, DirectMatrix(1, 2, 3))]);
        // Chunk data begins at 20; entry begins at +4; playback frames are entry +4.
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(28), 0);

        Assert.Null(N64CompressedAnimationBank.TryParse(bytes));
    }

    [Fact]
    public void Parse_RejectsNonZeroEntryReservedHalfword()
    {
        var bytes = BuildShell(PsxMeshFile.HierChunkV2Tag, [(3, ConstantChannels(1, 2, 3, 4, 5, 6))]);
        // chunk data begins at 20; entry begins at +4; reserved is entry +4.
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(28), 1);

        Assert.Null(N64CompressedAnimationBank.TryParse(bytes));
    }

    [Fact]
    public void Parse_RejectsOverlappingOrNonMonotonePoolOffsets()
    {
        var bytes = BuildShell(PsxMeshFile.HierChunkV2Tag,
        [
            (3, ConstantChannels(1, 2, 3, 4, 5, 6)),
            (3, ConstantChannels(7, 8, 9, 10, 11, 12))
        ]);
        // Second entry's BE pool offset equals the first one's.
        bytes.AsSpan(24, 4).CopyTo(bytes.AsSpan(32, 4));

        Assert.Null(N64CompressedAnimationBank.TryParse(bytes));
    }

    [Fact]
    public void DecodeSlot_CannotBorrowLastByteFromFollowingEntry()
    {
        var truncated = ConstantChannels(1, 2, 3, 4, 5, 6)[..^1];
        var bytes = BuildShell(PsxMeshFile.HierChunkV2Tag,
        [
            (3, truncated),
            (3, ConstantChannels(7, 8, 9, 10, 11, 12))
        ]);
        var bank = N64CompressedAnimationBank.TryParse(bytes);

        Assert.NotNull(bank);
        Assert.Throws<InvalidDataException>(() => bank!.DecodeSlot(0, 1));
        var second = bank!.DecodeSlot(1, 1);
        Assert.Equal(7, second.Channels[0, 0, 0]);
    }

    internal static byte[] BuildShell(
        uint chunkTag,
        IReadOnlyList<(ushort Frames, byte[] Payload)> clips)
    {
        return BuildShellChunks((chunkTag, BuildCompressedChunk(clips)));
    }

    internal static byte[] BuildDirectShell(
        IReadOnlyList<(ushort Frames, ushort Tween, byte[] Payload)> clips)
    {
        return BuildShellChunks((PsxMeshFile.HierChunkV1Tag, BuildDirectChunk(clips)));
    }

    private static byte[] BuildCompressedChunk(
        IReadOnlyList<(ushort Frames, byte[] Payload)> clips)
    {
        var tableLength = 4 + clips.Count * 8;
        var chunk = new byte[tableLength + clips.Sum(static clip => clip.Payload.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)clips.Count);
        var poolOffset = tableLength;
        for (var index = 0; index < clips.Count; index++)
        {
            var entry = chunk.AsSpan(4 + index * 8, 8);
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)poolOffset);
            BinaryPrimitives.WriteUInt16BigEndian(entry[4..], 0);
            BinaryPrimitives.WriteUInt16BigEndian(entry[6..], clips[index].Frames);
            clips[index].Payload.CopyTo(chunk.AsSpan(poolOffset));
            poolOffset += clips[index].Payload.Length;
        }

        return chunk;
    }

    private static byte[] BuildDirectChunk(
        IReadOnlyList<(ushort Frames, ushort Tween, byte[] Payload)> clips)
    {
        var tableLength = 4 + clips.Count * 8;
        var chunk = new byte[tableLength + clips.Sum(static clip => clip.Payload.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)clips.Count);
        var poolOffset = tableLength;
        for (var index = 0; index < clips.Count; index++)
        {
            var entry = chunk.AsSpan(4 + index * 8, 8);
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)poolOffset);
            BinaryPrimitives.WriteUInt16BigEndian(entry[4..], clips[index].Frames);
            BinaryPrimitives.WriteUInt16BigEndian(entry[6..], clips[index].Tween);
            clips[index].Payload.CopyTo(chunk.AsSpan(poolOffset));
            poolOffset += clips[index].Payload.Length;
        }

        return chunk;
    }

    private static byte[] BuildShellChunks(params (uint Tag, byte[] Data)[] chunks)
    {
        const int metaTop = 12;
        var bytes = new byte[metaTop + chunks.Sum(static chunk => 8 + chunk.Data.Length) + 4];

        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x0002_0004);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), metaTop);
        var cursor = metaTop;
        foreach (var (tag, data) in chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(cursor), tag);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(cursor + 4), (uint)data.Length);
            data.CopyTo(bytes.AsSpan(cursor + 8));
            cursor += 8 + data.Length;
        }

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(cursor), uint.MaxValue);
        return bytes;
    }

    private static byte[] DirectMatrices(params byte[][] matrices)
    {
        var bytes = new byte[matrices.Sum(static matrix => matrix.Length)];
        var cursor = 0;
        foreach (var matrix in matrices)
        {
            matrix.CopyTo(bytes.AsSpan(cursor));
            cursor += matrix.Length;
        }

        return bytes;
    }

    private static byte[] DirectMatrix(short x, short y, short z)
    {
        short[] cells =
        [
            4096, 0, 0,
            0, 4096, 0,
            0, 0, 4096,
            x, y, z
        ];
        var bytes = new byte[PsxAnimDecoder.DirectMatrixStrideBytes];
        for (var index = 0; index < cells.Length; index++)
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(index * 2), cells[index]);
        return bytes;
    }

    internal static byte[] ConstantChannels(params short[] values)
    {
        var bytes = new byte[values.Length * 3];
        for (var index = 0; index < values.Length; index++)
        {
            bytes[index * 3] = 0x0E;
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 3 + 1), values[index]);
        }

        return bytes;
    }
}
