using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

internal enum BinkAudioCarrierProfile
{
    Thps4Dee,
    Thps4Smo
}

internal sealed record BinkAudioCarrierInfo(
    int SampleRate,
    int Channels,
    uint FrameCount,
    double DurationSeconds,
    uint LargestFrameSize,
    uint MaximumDecodedAudioSize);

/// <summary>
///     Parser for the two measured THPS4-PC audio-only Bink dialects. The
///     exact 4x4/15 fps/one-track layout prevents ordinary Bink movies from
///     being claimed; each caller adds the tighter rate/flag profile belonging
///     to its own file family.
/// </summary>
internal static class BinkAudioCarrier
{
    private const int FixedHeaderSize = 44;
    private const int TrackMetadataSize = 12;
    private const int FrameIndexOffset = FixedHeaderSize + TrackMetadataSize;

    internal static BinkAudioCarrierInfo? Probe(
        ReadOnlySpan<byte> data,
        BinkAudioCarrierProfile profile)
    {
        if (data.Length < FrameIndexOffset + sizeof(uint)
            || !data[..4].SequenceEqual("BIKi"u8))
        {
            return null;
        }

        var declaredPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if ((long)declaredPayloadSize + 8 != data.Length)
            return null;

        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var largestFrameSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (frameCount == 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != frameCount
            || largestFrameSize == 0)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data[20..]) != 4
            || BinaryPrimitives.ReadUInt32LittleEndian(data[24..]) != 4
            || BinaryPrimitives.ReadUInt32LittleEndian(data[28..]) != 15
            || BinaryPrimitives.ReadUInt32LittleEndian(data[32..]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(data[36..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data[40..]) != 1)
        {
            return null;
        }

        var maximumDecodedAudioSize = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(data[48..]);
        var audioFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[50..]);
        var trackId = BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
        if (maximumDecodedAudioSize == 0
            || trackId != 0
            || !MatchesAudioProfile(profile, sampleRate, audioFlags))
        {
            return null;
        }

        var frameIndexEnd = FrameIndexOffset + ((long)frameCount + 1) * sizeof(uint);
        if (frameIndexEnd >= data.Length || frameIndexEnd > int.MaxValue)
            return null;

        long previousOffset = -1;
        long maximumFrameSpan = 0;
        for (var index = 0U; index <= frameCount; index++)
        {
            var indexPosition = FrameIndexOffset + (long)index * sizeof(uint);
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                data.Slice((int)indexPosition, sizeof(uint)));
            var offset = rawOffset & ~1U;

            if (index == 0 && ((rawOffset & 1) == 0 || offset != frameIndexEnd))
                return null;
            if (offset < frameIndexEnd || offset > data.Length || offset < previousOffset)
                return null;

            if (previousOffset >= 0)
                maximumFrameSpan = Math.Max(maximumFrameSpan, offset - previousOffset);
            previousOffset = offset;
        }

        if (previousOffset != data.Length || maximumFrameSpan != largestFrameSize)
            return null;

        return new BinkAudioCarrierInfo(
            sampleRate,
            audioFlags == 0x7000 ? 2 : 1,
            frameCount,
            frameCount / 15d,
            largestFrameSize,
            maximumDecodedAudioSize);
    }

    private static bool MatchesAudioProfile(
        BinkAudioCarrierProfile profile,
        int sampleRate,
        ushort flags)
    {
        return profile switch
        {
            BinkAudioCarrierProfile.Thps4Dee =>
                sampleRate is 11_025 or 22_050 or 44_100
                && flags is 0x5000 or 0x7000,
            BinkAudioCarrierProfile.Thps4Smo =>
                sampleRate is 44_100 or 48_000 && flags == 0x7000,
            _ => false
        };
    }
}
