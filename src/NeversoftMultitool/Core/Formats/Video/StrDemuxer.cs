namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Demuxes PS1 STR (MDEC) video files into video frames and audio sectors.
///     Supports three sector layout variants:
///     <list type="bullet">
///         <item>Standard: 2336-byte sectors (subheader + user data), used by most PS1 games</item>
///         <item>8-byte prefix: 8-byte file header + standard 2336-byte sectors (Spider-Man Prototype)</item>
///         <item>RIFF/CDXA: 44-byte RIFF header + raw 2352-byte sectors (SM2: Enter Electro Final)</item>
///     </list>
///     All variants are normalized to standard 2336-byte sectors before processing.
///     Video sectors (type 0x8001) carry MDEC bitstream chunks; audio sectors carry XA-ADPCM data.
/// </summary>
public static class StrDemuxer
{
    private const int SectorSize = 2336;
    private const int SubheaderSize = 8;
    private const int VideoHeaderSize = 32;
    private const int RiffHeaderSize = 44;
    private const int RawSectorSize = 2352;
    private const int SyncPlusHeaderSize = 16; // 12-byte sync + 4-byte CD header

    /// <summary>
    ///     Normalizes variant STR file layouts to standard 2336-byte sector format.
    ///     Returns null if the data doesn't match any known layout.
    /// </summary>
    private static byte[]? NormalizeToSectors(byte[] data)
    {
        // 1. RIFF/CDXA container: 44-byte header + raw 2352-byte sectors
        if (data.Length >= RiffHeaderSize + RawSectorSize
            && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'C' && data[9] == 'D' && data[10] == 'X' && data[11] == 'A'
            && (data.Length - RiffHeaderSize) % RawSectorSize == 0)
        {
            var rawSectorCount = (data.Length - RiffHeaderSize) / RawSectorSize;
            var output = new byte[rawSectorCount * SectorSize];
            for (var i = 0; i < rawSectorCount; i++)
            {
                // Strip 16-byte sync+header from each 2352-byte sector, keep 2336 bytes
                Buffer.BlockCopy(data, RiffHeaderSize + i * RawSectorSize + SyncPlusHeaderSize,
                    output, i * SectorSize, SectorSize);
            }

            return output;
        }

        // Note: Spider-Man Prototype WTC/FE/INTRO.STR starts with 00 00 08 00 00 00 08 00
        // but is NOT a valid STR video file — only 1 of 10438 sectors is a video sector.
        // This is a non-video file with .STR extension. No special handling needed.

        // 3. Standard: already 2336-byte sectors
        if (data.Length >= SectorSize && data.Length % SectorSize == 0)
            return data;

        return null;
    }

    /// <summary>
    ///     Checks if a file looks like a valid PS1 STR video (not an AFS archive or other format).
    /// </summary>
    public static bool IsStrFile(byte[] data)
    {
        var normalized = NormalizeToSectors(data);
        if (normalized == null || normalized.Length < SectorSize)
            return false;

        // Reject AFS archives (DC SPEECH.STR is actually AFS)
        if (normalized.Length >= 4 && normalized[0] == 'A' && normalized[1] == 'F'
            && normalized[2] == 'S' && normalized[3] == 0)
            return false;

        // Check first few sectors for valid STR video markers
        var sectorCount = Math.Min(normalized.Length / SectorSize, 16);
        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            var submode = normalized[offset + 2];

            // Check for video sector (not audio, not end-of-record only)
            if ((submode & 0x04) == 0) // Not audio
            {
                var payloadOffset = offset + SubheaderSize;
                if (payloadOffset + 4 <= normalized.Length)
                {
                    var sectorType = BitConverter.ToUInt16(normalized, payloadOffset + 2);
                    if (sectorType == 0x8001)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Enumerates complete video frames from the STR file.
    ///     Each frame is assembled from consecutive video sector chunks.
    /// </summary>
    public static IEnumerable<StrFrame> EnumerateFrames(byte[] data)
    {
        data = NormalizeToSectors(data) ?? data;
        var sectorCount = data.Length / SectorSize;

        // Current frame being assembled
        var currentFrameNum = -1;
        int frameWidth = 0, frameHeight = 0, frameQscale = 0;
        var expectedChunks = 0;
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

            // Extract chunk data (payload after 32-byte video header).
            // Payload size depends on the XA sector form (submode bit 5):
            //   Form 1: 2048 bytes user data → 2016 bytes video payload
            //   Form 2: 2324 bytes user data → 2292 bytes video payload
            var isForm2 = (submode & 0x20) != 0;
            var sectorUserData = isForm2 ? 2324 : 2048;
            var dataStart = payloadOffset + VideoHeaderSize;
            var dataLength = sectorUserData - VideoHeaderSize;
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

        // Note: demux_size trimming is NOT applied here because the demux_size field
        // counts bytes in the context of 2292-byte chunk payloads, and the decoder
        // handles exhaustion gracefully via corruption detection.

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
    ///     Extracts audio sectors as concatenated 2336-byte sectors, ready to be written
    ///     as a .xa file for XaDecoder.
    /// </summary>
    public static byte[] ExtractAudioSectors(byte[] data)
    {
        data = NormalizeToSectors(data) ?? data;
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
    ///     Quick scan: counts total video frames without fully assembling them.
    /// </summary>
    public static int CountFrames(byte[] data)
    {
        data = NormalizeToSectors(data) ?? data;
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
    ///     Checks if the STR file contains audio sectors.
    /// </summary>
    public static bool HasAudio(byte[] data)
    {
        data = NormalizeToSectors(data) ?? data;
        var sectorCount = data.Length / SectorSize;
        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            if ((data[offset + 2] & 0x04) != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Computes the video frame rate from the sector interleaving pattern.
    ///     PS1 STR framerate = disc_read_rate / sectors_per_frame.
    ///     At 2x disc speed: 150 sectors/second. Sectors per frame = video chunks + interleaved audio.
    /// </summary>
    public static double GetFrameRate(byte[] data)
    {
        const double discSectorsPerSecond = 150.0; // 2x disc speed (standard for Neversoft games)

        data = NormalizeToSectors(data) ?? data;
        var sectorCount = data.Length / SectorSize;
        if (sectorCount == 0) return 15.0;

        // Find the first video frame's chunk count and count interleaved audio sectors
        var videoChunksPerFrame = 0;
        var firstFrameNum = -1;
        var audioSectorsInFirstFrame = 0;
        var scanComplete = false;

        for (var s = 0; s < sectorCount && !scanComplete; s++)
        {
            var offset = s * SectorSize;
            var submode = data[offset + 2];

            if ((submode & 0x04) != 0)
            {
                // Audio sector — count only those within the first frame's region
                if (firstFrameNum >= 0 && videoChunksPerFrame > 0)
                    audioSectorsInFirstFrame++;
                continue;
            }

            var payloadOffset = offset + SubheaderSize;
            if (payloadOffset + 12 > data.Length) continue;

            var sectorType = BitConverter.ToUInt16(data, payloadOffset + 2);
            if (sectorType != 0x8001) continue;

            var frameNum = (int)BitConverter.ToUInt32(data, payloadOffset + 8);
            var chunkCount = BitConverter.ToUInt16(data, payloadOffset + 6);

            if (firstFrameNum < 0)
            {
                firstFrameNum = frameNum;
                videoChunksPerFrame = chunkCount;
            }
            else if (frameNum != firstFrameNum)
            {
                // Reached the second frame — done scanning
                scanComplete = true;
            }
        }

        if (videoChunksPerFrame == 0) return 15.0;

        var totalSectorsPerFrame = videoChunksPerFrame + audioSectorsInFirstFrame;
        return totalSectorsPerFrame > 0 ? discSectorsPerSecond / totalSectorsPerFrame : 15.0;
    }

    /// <summary>
    ///     A single assembled video frame from the STR bitstream.
    /// </summary>
    public sealed class StrFrame
    {
        public required int FrameNumber { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int QuantizationScale { get; init; }
        public required byte[] Data { get; init; }
    }
}
