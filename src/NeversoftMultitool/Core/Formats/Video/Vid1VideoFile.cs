using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Video;

public sealed class Vid1VideoFile
{
    private const int HeadChildOffset = 0x0C;
    private const int FrameChildOffset = 0x20;
    private const int ViddFlagSeedOffset = 4;
    private const int ViddTag16Offset = 6;

    private static readonly byte[] ZigzagScan =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

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

        if (!TryFindChunk(data, headChunk.Offset + HeadChildOffset, headChunk.EndOffset, "VIDH", out var vidhChunk,
                out error))
            return false;

        if (!TryParseVideoHeader(data, vidhChunk, out var width, out var height, out var frameCount,
                out var frameRateNumerator, out var frameRateDenominator, out error))
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

        if (!string.IsNullOrWhiteSpace(sourceName) &&
            Path.GetFileNameWithoutExtension(sourceName).Equals("intro", StringComparison.OrdinalIgnoreCase))
        {
            return Vid1VideoVariant.ThawLongForm;
        }

        var tag16Values = frames.Select(static frame => frame.Tag16).ToArray();
        var longFormCount =
            tag16Values.Count(static tag16 => tag16 is 0x2002 or 0x4014 or 0x4024 or 0x5014 or 0x5024 or 0x5044);
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
        int? spriteWarpAccuracy = null;

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

                if (!TryParseVideoFrame(
                        data,
                        childChunk,
                        frames.Count,
                        ref spritePointCount,
                        ref spriteWarpAccuracy,
                        out var frame,
                        out error))
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
        ref int? spriteWarpAccuracy,
        out Vid1VideoFrame frame,
        out string error)
    {
        error = "";
        var payload = data.AsSpan(chunk.Offset + 8, chunk.EndOffset - (chunk.Offset + 8)).ToArray();
        if (payload.Length < ViddTag16Offset + 2)
        {
            error = "VIDD payload is too small";
            frame = default!;
            return false;
        }

        var tag16 = ReadUInt16BigEndian(payload, ViddTag16Offset);
        var usesCustomQuantMatrices = false;
        var isPartial = false;
        var codedPayload = Array.Empty<byte>();
        var bitstream = Array.Empty<byte>();
        var intraDcThresholdIndex = 0;
        var quantizer = 0;
        int? forwardCode = null;
        int? backwardCode = null;
        uint? currentFrameStateWord = null;
        uint? alternateFrameStateWord = null;
        var preambleClass = -1;
        var specialCallerGate = false;
        var flagBitOffset = 0;
        var vlcBitOffset = 0;
        byte[]? customIntraMatrix = null;
        byte[]? customInterMatrix = null;
        int[]? spriteTrajectoryDeltas = null;

        try
        {
            // FUN_80166C80 passes VIDD+0x0C to FUN_8029978C, which is payload+0x04
            // in this parser. FUN_8029BFAC then stores ctx+0x8C = ctx+0x30, so
            // the frame header and macroblock VLC/control reads advance one shared
            // four-word reader. Tag16 at payload+0x06 is part of that bitstream.
            //
            // Keep the trailing VIDD bytes in both Bitstream and CodedPayload.
            // The GameCube reader is a multiword prefetch reader, and stripping
            // those bytes causes early EOF/implicit-skip behavior near the tail
            // of some frames (most visibly credits.vid motion text).
            if (payload.Length < ViddFlagSeedOffset)
                throw new EndOfStreamException("VIDD payload is too small");

            bitstream = payload.AsSpan(ViddFlagSeedOffset).ToArray();
            var reader = new Vid1BitReader(bitstream);

            // Layout ported from FUN_8029C2F8. The outer optional-header flag
            // gates the sprite config, quant_type + matrix flags, stateFlag3c,
            // and the trailing discard bit. A separate 1-bit caller gate then
            // sits outside that block before threshold/quantizer.
            _ = reader.ReadBits(16);
            preambleClass = reader.ReadBits(2);
            var hasOptionalHeader = reader.ReadFlag();

            var stateFlag3c = false;
            if (hasOptionalHeader)
            {
                var spriteConfigPresent = reader.ReadFlag();
                if (spriteConfigPresent)
                {
                    spritePointCount = reader.ReadBits(2);
                    spriteWarpAccuracy = reader.ReadBits(2);
                }

                usesCustomQuantMatrices = reader.ReadFlag();
                if (usesCustomQuantMatrices)
                {
                    if (reader.ReadFlag())
                        customIntraMatrix = ParseMatrix(reader);
                    if (reader.ReadFlag())
                        customInterMatrix = ParseMatrix(reader);
                }

                stateFlag3c = reader.ReadFlag();
                _ = reader.ReadFlag();
            }

            specialCallerGate = reader.ReadFlag();
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
                spriteTrajectoryDeltas = new int[spritePointCount.Value * 2];
                for (var i = 0; i < spritePointCount.Value; i++)
                {
                    var dx = reader.ReadBits(14);
                    if (reader.ReadFlag())
                        dx = -dx;

                    var dy = reader.ReadBits(14);
                    if (reader.ReadFlag())
                        dy = -dy;

                    spriteTrajectoryDeltas[i * 2] = dx;
                    spriteTrajectoryDeltas[i * 2 + 1] = dy;
                }
            }

            if (stateFlag3c)
            {
                _ = reader.ReadFlag();
                _ = reader.ReadFlag();
            }

            reader.AlignToNextByte();
            flagBitOffset = reader.BitPosition;
            vlcBitOffset = reader.BitPosition;
            var codedDataOffset = ViddFlagSeedOffset + reader.BytesConsumed;
            var codedDataEnd = payload.Length;
            if (codedDataOffset > codedDataEnd)
                throw new EndOfStreamException("VIDD coded payload is truncated");

            // CodedPayload is the post-header VLC window. A separate flag
            // reader starts from Bitstream at the same post-header bit offset.
            vlcBitOffset = 0;
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
            bitstream,
            intraDcThresholdIndex,
            quantizer,
            forwardCode,
            backwardCode,
            currentFrameStateWord,
            alternateFrameStateWord,
            specialCallerGate,
            customIntraMatrix,
            customInterMatrix)
        {
            FlagBitOffset = flagBitOffset,
            VlcBitOffset = vlcBitOffset,
            SpritePointCount = spritePointCount,
            SpriteWarpAccuracy = spriteWarpAccuracy,
            SpriteTrajectoryDeltas = spriteTrajectoryDeltas
        };
        return true;
    }

    // Custom quant matrix layout per FUN_8029C2F8 (m4decoder_decompiled.c:8754-8824):
    // entries read 8 bits each, written through the MPEG-4 zigzag scan, zero-
    // terminated. After a zero, the last-non-zero value fills the remaining
    // (scan-ordered) positions. See vid1_fun_8029c650_sprite.md memory and
    // MPEG-4 §7.4.7 (load_intra_quant_mat).
    private static byte[] ParseMatrix(Vid1BitReader reader)
    {
        var zigzag = ZigzagScan;
        var output = new byte[64];
        var prev = 0;
        var i = 0;
        for (; i < 64; i++)
        {
            var value = reader.ReadBits(8);
            output[zigzag[i]] = (byte)value;
            if (value == 0)
                break;
            prev = value;
        }

        for (; i < 64; i++)
            output[zigzag[i]] = (byte)prev;

        return output;
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
}
