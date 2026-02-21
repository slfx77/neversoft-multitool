namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
/// Demuxes PS1 STR (MDEC) video files into video frames and audio sectors.
/// STR files contain 2336-byte sectors (CD-ROM Mode 2 Form 2 without sync header).
/// Each sector has an 8-byte XA subheader followed by 2328 bytes of payload.
/// Video sectors (type 0x8001) carry MDEC bitstream chunks; audio sectors carry XA-ADPCM data.
/// </summary>
public static class StrDemuxer
{
    private const int SectorSize = 2336;
    private const int SubheaderSize = 8;
    private const int VideoHeaderSize = 32;

    /// <summary>
    /// A single assembled video frame from the STR bitstream.
    /// </summary>
    public sealed class StrFrame
    {
        public required int FrameNumber { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int QuantizationScale { get; init; }
        public required byte[] Data { get; init; }
    }

    /// <summary>
    /// Checks if a file looks like a valid PS1 STR video (not an AFS archive or other format).
    /// </summary>
    public static bool IsStrFile(byte[] data)
    {
        if (data.Length < SectorSize || data.Length % SectorSize != 0)
            return false;

        // Reject AFS archives (DC SPEECH.STR is actually AFS)
        if (data.Length >= 4 && data[0] == 'A' && data[1] == 'F' && data[2] == 'S' && data[3] == 0)
            return false;

        // Check first few sectors for valid STR video markers
        var sectorCount = Math.Min(data.Length / SectorSize, 16);
        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            var submode = data[offset + 2];

            // Check for video sector (not audio, not end-of-record only)
            if ((submode & 0x04) == 0) // Not audio
            {
                var payloadOffset = offset + SubheaderSize;
                if (payloadOffset + 4 <= data.Length)
                {
                    var sectorType = BitConverter.ToUInt16(data, payloadOffset + 2);
                    if (sectorType == 0x8001)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates complete video frames from the STR file.
    /// Each frame is assembled from consecutive video sector chunks.
    /// </summary>
    public static IEnumerable<StrFrame> EnumerateFrames(byte[] data)
    {
        var sectorCount = data.Length / SectorSize;

        // Current frame being assembled
        int currentFrameNum = -1;
        int frameWidth = 0, frameHeight = 0, frameQscale = 0;
        int expectedChunks = 0;
        var frameChunks = new SortedDictionary<int, byte[]>();

        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            var submode = data[offset + 2];

            // Skip audio sectors (submode bit 2)
            if ((submode & 0x04) != 0)
                continue;

            var payloadOffset = offset + SubheaderSize;
            if (payloadOffset + VideoHeaderSize > data.Length)
                continue;

            // Video sector header (32 bytes):
            // u16: status (always 0x0160 for Neversoft STR)
            // u16: type (0x8001 = video)
            // u16: sector number within frame (chunk index)
            // u16: total sectors for this frame (chunk count)
            // u32: frame number
            // u32: demuxed byte size of frame
            // u16: width
            // u16: height
            // ... rest of header (quantization scale is in the bitstream header, not here)

            var sectorType = BitConverter.ToUInt16(data, payloadOffset + 2);
            if (sectorType != 0x8001)
                continue;

            var chunkIndex = BitConverter.ToUInt16(data, payloadOffset + 4);
            var chunkCount = BitConverter.ToUInt16(data, payloadOffset + 6);
            var frameNum = (int)BitConverter.ToUInt32(data, payloadOffset + 8);
            var width = BitConverter.ToUInt16(data, payloadOffset + 16);
            var height = BitConverter.ToUInt16(data, payloadOffset + 18);

            // New frame started?
            if (frameNum != currentFrameNum)
            {
                // Yield the previous frame if complete
                if (currentFrameNum >= 0 && frameChunks.Count == expectedChunks)
                {
                    yield return AssembleFrame(currentFrameNum, frameWidth, frameHeight, frameQscale, frameChunks);
                }

                currentFrameNum = frameNum;
                frameWidth = width;
                frameHeight = height;
                expectedChunks = chunkCount;
                frameChunks.Clear();
            }

            // Extract chunk data (payload after 32-byte video header)
            var dataStart = payloadOffset + VideoHeaderSize;
            var dataLength = SectorSize - SubheaderSize - VideoHeaderSize;
            if (dataStart + dataLength > data.Length)
                dataLength = data.Length - dataStart;

            var chunkData = new byte[dataLength];
            Buffer.BlockCopy(data, dataStart, chunkData, 0, dataLength);
            frameChunks[chunkIndex] = chunkData;
        }

        // Yield final frame
        if (currentFrameNum >= 0 && frameChunks.Count == expectedChunks)
        {
            yield return AssembleFrame(currentFrameNum, frameWidth, frameHeight, frameQscale, frameChunks);
        }
    }

    private static StrFrame AssembleFrame(int frameNum, int width, int height, int qscale,
        SortedDictionary<int, byte[]> chunks)
    {
        // Concatenate chunks in order
        var totalSize = 0;
        foreach (var chunk in chunks.Values)
            totalSize += chunk.Length;

        var assembled = new byte[totalSize];
        var pos = 0;
        foreach (var chunk in chunks.Values)
        {
            Buffer.BlockCopy(chunk, 0, assembled, pos, chunk.Length);
            pos += chunk.Length;
        }

        // Read quantization scale from assembled bitstream header (bytes 4-5)
        var bitstreamQscale = assembled.Length >= 6
            ? BitConverter.ToUInt16(assembled, 4)
            : qscale;

        return new StrFrame
        {
            FrameNumber = frameNum,
            Width = width,
            Height = height,
            QuantizationScale = bitstreamQscale,
            Data = assembled
        };
    }

    /// <summary>
    /// Extracts audio sectors as concatenated 2336-byte sectors, ready to be written
    /// as a .xa file for XaDecoder.
    /// </summary>
    public static byte[] ExtractAudioSectors(byte[] data)
    {
        var sectorCount = data.Length / SectorSize;
        var audioSectors = new List<byte[]>();

        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            var submode = data[offset + 2];

            // Audio sector: submode bit 2 set
            if ((submode & 0x04) != 0)
            {
                var sector = new byte[SectorSize];
                Buffer.BlockCopy(data, offset, sector, 0, SectorSize);
                audioSectors.Add(sector);
            }
        }

        if (audioSectors.Count == 0)
            return [];

        var result = new byte[audioSectors.Count * SectorSize];
        for (var i = 0; i < audioSectors.Count; i++)
            Buffer.BlockCopy(audioSectors[i], 0, result, i * SectorSize, SectorSize);

        return result;
    }

    /// <summary>
    /// Quick scan: counts total video frames without fully assembling them.
    /// </summary>
    public static int CountFrames(byte[] data)
    {
        var sectorCount = data.Length / SectorSize;
        var maxFrame = -1;

        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            var submode = data[offset + 2];

            if ((submode & 0x04) != 0) continue; // skip audio

            var payloadOffset = offset + SubheaderSize;
            if (payloadOffset + 12 > data.Length) continue;

            var sectorType = BitConverter.ToUInt16(data, payloadOffset + 2);
            if (sectorType != 0x8001) continue;

            var frameNum = (int)BitConverter.ToUInt32(data, payloadOffset + 8);
            if (frameNum > maxFrame) maxFrame = frameNum;
        }

        return maxFrame + 1; // Frame numbers are 0-based
    }

    /// <summary>
    /// Checks if the STR file contains audio sectors.
    /// </summary>
    public static bool HasAudio(byte[] data)
    {
        var sectorCount = data.Length / SectorSize;
        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            if ((data[offset + 2] & 0x04) != 0)
                return true;
        }

        return false;
    }
}
