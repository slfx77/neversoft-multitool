using System.Buffers.Binary;
using System.Text;
using System.Linq;

namespace NeversoftMultitool.Core.Formats.Video;

public enum Vid1VideoVariant
{
    Unknown,
    ThawLongForm,
    ThawAtvi
}

public sealed record Vid1VideoFrame(
    int Index,
    ushort Tag16,
    int PreambleClass,
    bool UsesCustomQuantMatrices,
    bool IsPartial,
    byte[] CodedPayload,
    int IntraDcThresholdIndex,
    int Quantizer,
    int? ForwardCode,
    int? BackwardCode,
    uint? CurrentFrameStateWord,
    uint? AlternateFrameStateWord,
    bool HasSpecialCallerGate)
{
    internal int GetFallbackVopType()
    {
        return PreambleClass switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            _ => 0
        };
    }
}

public sealed class Vid1VideoFile
{
    private const int HeadChildOffset = 0x0C;
    private const int FrameChildOffset = 0x20;
    private const int ViddCustomHeaderOffset = 12;
    private const int ViddTailSize = 8;

    private Vid1VideoFile(
        string? sourcePath,
        int width,
        int height,
        int frameCount,
        int frameRateNumerator,
        int frameRateDenominator,
        Vid1VideoVariant variant,
        IReadOnlyList<Vid1VideoFrame> frames)
    {
        SourcePath = sourcePath;
        Width = width;
        Height = height;
        FrameCount = frameCount;
        FrameRateNumerator = frameRateNumerator;
        FrameRateDenominator = frameRateDenominator;
        Variant = variant;
        Frames = frames;
    }

    public string? SourcePath { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }
    public int FrameRateNumerator { get; }
    public int FrameRateDenominator { get; }
    public Vid1VideoVariant Variant { get; }
    public IReadOnlyList<Vid1VideoFrame> Frames { get; }

    public double FrameRate =>
        FrameRateDenominator > 0
            ? FrameRateNumerator / (double)FrameRateDenominator
            : 0.0;

    public TimeSpan Duration =>
        FrameRate > 0
            ? TimeSpan.FromSeconds(FrameCount / FrameRate)
            : TimeSpan.Zero;

    public static Vid1VideoFile Parse(string inputPath)
    {
        if (!TryParse(inputPath, out var file, out var error))
            throw new InvalidOperationException(error);

        return file!;
    }

    public static bool TryParse(string inputPath, out Vid1VideoFile? file, out string error)
    {
        file = null;

        try
        {
            var data = File.ReadAllBytes(inputPath);
            if (!TryParse(data, Path.GetFileName(inputPath), out file, out error))
                return false;

            file = new Vid1VideoFile(
                inputPath,
                file!.Width,
                file.Height,
                file.FrameCount,
                file.FrameRateNumerator,
                file.FrameRateDenominator,
                file.Variant,
                file.Frames);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryParse(
        byte[] data,
        string? sourceName,
        out Vid1VideoFile? file,
        out string error)
    {
        file = null;

        if (!TryReadChunk(data, 0, data.Length, out var rootChunk, out error))
            return false;

        if (rootChunk.Tag != "VID1")
        {
            error = "Not a VID1 file";
            return false;
        }

        if (!TryReadChunk(data, rootChunk.EndOffset, data.Length, out var headChunk, out error))
            return false;

        if (headChunk.Tag != "HEAD")
        {
            error = "VID1 HEAD chunk not found";
            return false;
        }

        if (!TryFindChunk(data, headChunk.Offset + HeadChildOffset, headChunk.EndOffset, "VIDH", out var vidhChunk, out error))
            return false;

        if (!TryParseVideoHeader(data, vidhChunk, out var width, out var height, out var frameCount, out var frameRateNumerator, out var frameRateDenominator, out error))
            return false;

        if (!TryParseFrames(data, headChunk.EndOffset, out var frames, out error))
            return false;

        if (frames.Count == 0)
        {
            error = "VID1 video frames were not found";
            return false;
        }

        var variant = ClassifyVariant(sourceName, frames);
        file = new Vid1VideoFile(
            null,
            width,
            height,
            frameCount,
            frameRateNumerator,
            frameRateDenominator,
            variant,
            frames);
        error = "";
        return true;
    }

    private static Vid1VideoVariant ClassifyVariant(string? sourceName, IReadOnlyList<Vid1VideoFrame> frames)
    {
        if (!string.IsNullOrWhiteSpace(sourceName) &&
            Path.GetFileNameWithoutExtension(sourceName).Equals("atvi", StringComparison.OrdinalIgnoreCase))
        {
            return Vid1VideoVariant.ThawAtvi;
        }

        var tag16Values = frames.Select(static frame => frame.Tag16).ToArray();
        var longFormCount = tag16Values.Count(static tag16 => tag16 is 0x2002 or 0x4014 or 0x4024 or 0x5014 or 0x5024 or 0x5044);
        var atviCount = tag16Values.Count(static tag16 => tag16 is 0x4016 or 0x5016 or 0x8026 or 0x8029 or 0x8046);

        if (atviCount > longFormCount && atviCount > 0)
            return Vid1VideoVariant.ThawAtvi;

        if (longFormCount > 0)
            return Vid1VideoVariant.ThawLongForm;

        return Vid1VideoVariant.Unknown;
    }

    private static bool TryParseVideoHeader(
        byte[] data,
        Vid1Chunk chunk,
        out int width,
        out int height,
        out int frameCount,
        out int frameRateNumerator,
        out int frameRateDenominator,
        out string error)
    {
        width = 0;
        height = 0;
        frameCount = 0;
        frameRateNumerator = 0;
        frameRateDenominator = 0;

        if (chunk.Size < 0x20)
        {
            error = "VIDH chunk is too small";
            return false;
        }

        var baseOffset = chunk.Offset + 8;
        width = ReadUInt16BigEndian(data, baseOffset + 0x04);
        height = ReadUInt16BigEndian(data, baseOffset + 0x06);
        frameCount = checked((int)ReadUInt32BigEndian(data, baseOffset + 0x08));
        frameRateNumerator = checked((int)ReadUInt32BigEndian(data, baseOffset + 0x10));
        frameRateDenominator = ReadUInt16BigEndian(data, baseOffset + 0x14);

        if (width <= 0 || height <= 0)
        {
            error = "VID1 dimensions are invalid";
            return false;
        }

        if (frameCount <= 0)
        {
            error = "VID1 frame count is invalid";
            return false;
        }

        if (frameRateNumerator <= 0 || frameRateDenominator <= 0)
        {
            error = "VID1 frame rate is invalid";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryParseFrames(
        byte[] data,
        int firstFrameOffset,
        out List<Vid1VideoFrame> frames,
        out string error)
    {
        frames = [];
        error = "";
        var offset = firstFrameOffset;
        int? spritePointCount = null;

        while (offset + 8 <= data.Length)
        {
            if (TryIsZeroPadding(data, offset, data.Length))
                break;

            if (!TryReadChunk(data, offset, data.Length, out var frameChunk, out error))
                return false;

            if (frameChunk.Tag != "FRAM")
                break;

            var childOffset = frameChunk.Offset + FrameChildOffset;
            while (childOffset + 8 <= frameChunk.EndOffset)
            {
                if (!TryReadChunk(data, childOffset, frameChunk.EndOffset, out var childChunk, out error))
                    return false;

                childOffset = childChunk.EndOffset;
                if (childChunk.Tag != "VIDD")
                    continue;

                if (!TryParseVideoFrame(data, childChunk, frames.Count, ref spritePointCount, out var frame, out error))
                    return false;

                frames.Add(frame);
            }

            offset = frameChunk.EndOffset;
        }

        return true;
    }

    private static bool TryParseVideoFrame(
        byte[] data,
        Vid1Chunk chunk,
        int frameIndex,
        ref int? spritePointCount,
        out Vid1VideoFrame frame,
        out string error)
    {
        error = "";
        var payload = data.AsSpan(chunk.Offset + 8, chunk.EndOffset - (chunk.Offset + 8)).ToArray();
        if (payload.Length < ViddCustomHeaderOffset + ViddTailSize)
        {
            error = "VIDD payload is too small";
            frame = default!;
            return false;
        }

        var tag16 = ReadUInt16BigEndian(payload, 6);
        var usesCustomQuantMatrices = false;
        var isPartial = false;
        var codedPayload = Array.Empty<byte>();
        var intraDcThresholdIndex = 0;
        var quantizer = 0;
        int? forwardCode = null;
        int? backwardCode = null;
        uint? currentFrameStateWord = null;
        uint? alternateFrameStateWord = null;
        var preambleClass = -1;

        try
        {
            var bitstream = payload.AsSpan(ViddCustomHeaderOffset, payload.Length - ViddCustomHeaderOffset - ViddTailSize).ToArray();
            var reader = new Vid1BitReader(bitstream);

            _ = reader.ReadBits(16);
            preambleClass = reader.ReadBits(2);
            var hasOptionalHeader = reader.ReadFlag();

            if (hasOptionalHeader)
            {
                var spriteConfigPresent = reader.ReadFlag();
                if (spriteConfigPresent)
                {
                    spritePointCount = reader.ReadBits(2);
                    _ = reader.ReadBits(2);
                }
            }

            usesCustomQuantMatrices = reader.ReadFlag();
            if (usesCustomQuantMatrices)
            {
                if (reader.ReadFlag())
                    ParseMatrix(reader);
                if (reader.ReadFlag())
                    ParseMatrix(reader);
            }

            var stateFlag3c = reader.ReadFlag();
            _ = reader.ReadFlag();
            intraDcThresholdIndex = reader.ReadBits(3);
            quantizer = reader.ReadBits(5);

            if (preambleClass != 0)
                forwardCode = reader.ReadBits(3);
            if (preambleClass == 2)
                backwardCode = reader.ReadBits(3);

            if (preambleClass == 2)
                alternateFrameStateWord = reader.ReadBitsUInt32();
            else
                currentFrameStateWord = reader.ReadBitsUInt32();

            if (preambleClass == 3 && spritePointCount.HasValue)
            {
                for (var i = 0; i < spritePointCount.Value; i++)
                {
                    _ = reader.ReadBits(14);
                    _ = reader.ReadFlag();
                    _ = reader.ReadBits(14);
                    _ = reader.ReadFlag();
                }
            }

            if (stateFlag3c)
            {
                _ = reader.ReadFlag();
                _ = reader.ReadFlag();
            }

            reader.AlignToNextByte();
            var codedDataOffset = ViddCustomHeaderOffset + reader.BytesConsumed;
            var codedDataEnd = payload.Length - ViddTailSize;
            if (codedDataOffset > codedDataEnd)
                throw new EndOfStreamException("VIDD coded payload is truncated");

            codedPayload = payload.AsSpan(codedDataOffset, codedDataEnd - codedDataOffset).ToArray();
        }
        catch (EndOfStreamException)
        {
            isPartial = true;
        }

        frame = new Vid1VideoFrame(
            frameIndex,
            tag16,
            preambleClass,
            usesCustomQuantMatrices,
            isPartial,
            codedPayload,
            intraDcThresholdIndex,
            quantizer,
            forwardCode,
            backwardCode,
            currentFrameStateWord,
            alternateFrameStateWord,
            codedPayload.Length > 0 && (codedPayload[0] & 0x80) != 0);
        return true;
    }

    private static void ParseMatrix(Vid1BitReader reader)
    {
        while (reader.ReadBits(8) != 0)
        {
            // Matrix entries are zero-terminated; the deterministic parser only needs to consume them.
        }
    }

    private static bool TryFindChunk(
        byte[] data,
        int startOffset,
        int endOffset,
        string tag,
        out Vid1Chunk chunk,
        out string error)
    {
        chunk = default;
        error = "";
        var currentOffset = startOffset;

        while (currentOffset + 8 <= endOffset)
        {
            if (TryIsZeroPadding(data, currentOffset, endOffset))
                break;

            if (!TryReadChunk(data, currentOffset, endOffset, out var candidate, out error))
                return false;

            if (candidate.Tag == tag)
            {
                chunk = candidate;
                return true;
            }

            currentOffset = candidate.EndOffset;
        }

        error = $"VID1 chunk {tag} not found";
        return false;
    }

    private static bool TryReadChunk(
        byte[] data,
        int offset,
        int limit,
        out Vid1Chunk chunk,
        out string error)
    {
        chunk = default;

        if (offset < 0 || offset + 8 > limit || offset + 8 > data.Length)
        {
            error = "VID1 chunk header is truncated";
            return false;
        }

        var size = checked((int)ReadUInt32BigEndian(data, offset + 4));
        if (size < 8)
        {
            error = "VID1 chunk size is invalid";
            return false;
        }

        var endOffset = offset + size;
        if (endOffset > limit || endOffset > data.Length)
        {
            error = "VID1 chunk extends beyond the file";
            return false;
        }

        chunk = new Vid1Chunk(
            Encoding.ASCII.GetString(data, offset, 4),
            offset,
            size,
            endOffset);
        error = "";
        return true;
    }

    private static bool TryIsZeroPadding(byte[] data, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (data[i] != 0)
                return false;
        }

        return true;
    }

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    }

    private readonly record struct Vid1Chunk(
        string Tag,
        int Offset,
        int Size,
        int EndOffset);

    private sealed class Vid1BitReader(byte[] data)
    {
        private int _bitPosition;

        public int BytesConsumed => (_bitPosition + 7) / 8;

        public int ReadBits(int bitCount)
        {
            if (bitCount < 0 || _bitPosition + bitCount > data.Length * 8)
                throw new EndOfStreamException("VID1 bitstream is truncated");

            var value = 0;
            for (var i = 0; i < bitCount; i++)
            {
                var byteIndex = _bitPosition >> 3;
                var bitIndex = 7 - (_bitPosition & 7);
                value = (value << 1) | ((data[byteIndex] >> bitIndex) & 1);
                _bitPosition++;
            }

            return value;
        }

        public uint ReadBitsUInt32()
        {
            const int bitCount = 32;
            if (_bitPosition + bitCount > data.Length * 8)
                throw new EndOfStreamException("VID1 bitstream is truncated");

            uint value = 0;
            for (var i = 0; i < bitCount; i++)
            {
                var byteIndex = _bitPosition >> 3;
                var bitIndex = 7 - (_bitPosition & 7);
                value = (value << 1) | (uint)((data[byteIndex] >> bitIndex) & 1);
                _bitPosition++;
            }

            return value;
        }

        public bool ReadFlag()
        {
            return ReadBits(1) != 0;
        }

        public void AlignToNextByte()
        {
            if ((_bitPosition & 7) != 0)
                _bitPosition += 8 - (_bitPosition & 7);
        }
    }
}
