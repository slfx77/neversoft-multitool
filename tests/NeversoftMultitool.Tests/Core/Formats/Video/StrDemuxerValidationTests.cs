using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class StrDemuxerValidationTests
{
    private const int SectorSize = 2336;
    private const int VideoHeaderOffset = 8;

    [Theory]
    [InlineData(0, 16)]
    [InlineData(16, 0)]
    public void ZeroDimensionVideoSector_IsNotRecognizedAsAFrame(int width, int height)
    {
        var data = BuildSingleFrameSector((ushort)width, (ushort)height);

        Assert.False(StrDemuxer.IsStrFile(data));
        Assert.Empty(StrDemuxer.EnumerateFrames(data));
        Assert.Equal(0, StrDemuxer.CountFrames(data));
        Assert.Equal(15.0, StrDemuxer.GetFrameRate(data));
    }

    [Fact]
    public void PositiveDimensionsVideoSector_IsRecognizedAsAFrame()
    {
        var data = BuildSingleFrameSector(16, 16);

        Assert.True(StrDemuxer.IsStrFile(data));
        var frame = Assert.Single(StrDemuxer.EnumerateFrames(data));
        Assert.Equal(16, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal(1, StrDemuxer.CountFrames(data));
        Assert.Equal(150.0, StrDemuxer.GetFrameRate(data));
    }

    [Fact]
    public void WrongStatusVideoSector_IsNotRecognizedAsAFrame()
    {
        var data = BuildSingleFrameSector(16, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(VideoHeaderOffset), 0);

        Assert.False(StrDemuxer.IsStrFile(data));
        Assert.Empty(StrDemuxer.EnumerateFrames(data));
        Assert.Equal(0, StrDemuxer.CountFrames(data));
        Assert.Equal(15.0, StrDemuxer.GetFrameRate(data));
    }

    [Fact]
    public void SameFrameWithConflictingChunkCounts_IsNotEnumeratedOrCounted()
    {
        var data = BuildChunkSequence((0, 2, 7), (1, 1, 7));

        Assert.Empty(StrDemuxer.EnumerateFrames(data));
        Assert.Equal(0, StrDemuxer.CountFrames(data));
    }

    [Fact]
    public void ConflictingChunkCountState_ResetsForTheNextFrame()
    {
        var data = BuildChunkSequence((0, 2, 7), (1, 1, 7), (0, 1, 8));

        var frame = Assert.Single(StrDemuxer.EnumerateFrames(data));
        Assert.Equal(8, frame.FrameNumber);
        Assert.Equal(1, StrDemuxer.CountFrames(data));
    }

    private static byte[] BuildSingleFrameSector(ushort width, ushort height)
    {
        var data = new byte[SectorSize];
        data[2] = 0x48;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(VideoHeaderOffset), 0x0160);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(VideoHeaderOffset + 2), 0x8001);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(VideoHeaderOffset + 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(VideoHeaderOffset + 6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(VideoHeaderOffset + 8), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(VideoHeaderOffset + 12), 2016);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(VideoHeaderOffset + 16), width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(VideoHeaderOffset + 18), height);
        return data;
    }

    private static byte[] BuildChunkSequence(
        params (ushort ChunkIndex, ushort ChunkCount, uint FrameNumber)[] chunks)
    {
        var data = new byte[SectorSize * chunks.Length];
        for (var sectorIndex = 0; sectorIndex < chunks.Length; sectorIndex++)
        {
            var (chunkIndex, chunkCount, frameNumber) = chunks[sectorIndex];
            var sectorOffset = sectorIndex * SectorSize;
            var headerOffset = sectorOffset + VideoHeaderOffset;
            data[sectorOffset + 2] = 0x48;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(headerOffset), 0x0160);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(headerOffset + 2), 0x8001);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(headerOffset + 4), chunkIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(headerOffset + 6), chunkCount);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(headerOffset + 8), frameNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(headerOffset + 12), 2016);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(headerOffset + 16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(headerOffset + 18), 16);
        }

        return data;
    }
}
