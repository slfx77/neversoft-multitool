using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

internal sealed record Vid1SyntheticVideoFrameSpec(
    ushort Tag16,
    int PreambleClass = 0,
    int IntraDcThresholdIndex = 0,
    int Quantizer = 2,
    int? ForwardCode = null,
    int? BackwardCode = null,
    uint? CurrentFrameStateWord = null,
    uint? AlternateFrameStateWord = null,
    bool HasSpecialCallerGate = false,
    bool UsesCustomQuantMatrices = false,
    bool StateFlag3c = false,
    byte[]? CodedPayload = null);

internal static class Vid1VideoTestBuilder
{
    public static byte[] CreateVideoVid1(
        int width = 512,
        int height = 384,
        int frameRateNumerator = 30000,
        int frameRateDenominator = 1001,
        IReadOnlyList<Vid1SyntheticVideoFrameSpec>? frames = null)
    {
        var resolvedFrames = frames?.Count > 0
            ? frames.ToArray()
            : new[]
            {
                new Vid1SyntheticVideoFrameSpec(
                    0x4014,
                    PreambleClass: 0,
                    Quantizer: 7,
                    CurrentFrameStateWord: 0x11223344,
                    HasSpecialCallerGate: true,
                    CodedPayload: [0x11, 0x22, 0x33, 0x44])
            };

        var rootChunk = BuildChunk("VID1", new byte[0x18]);
        var vidhChunk = CreateVidhChunk(width, height, resolvedFrames.Length, frameRateNumerator, frameRateDenominator);
        var headPayload = new byte[4 + vidhChunk.Length];
        vidhChunk.CopyTo(headPayload.AsSpan(4));
        var headChunk = BuildChunk("HEAD", headPayload);

        using var stream = new MemoryStream();
        stream.Write(rootChunk);
        stream.Write(headChunk);

        foreach (var frame in resolvedFrames)
            stream.Write(CreateFrameChunk(frame));

        return stream.ToArray();
    }

    public static byte[] CreateVidhChunk(
        int width = 512,
        int height = 384,
        int frameCount = 1,
        int frameRateNumerator = 30000,
        int frameRateDenominator = 1001)
    {
        var payload = new byte[0x20];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0x04, 2), checked((ushort)width));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0x06, 2), checked((ushort)height));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x08, 4), checked((uint)frameCount));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x10, 4), checked((uint)frameRateNumerator));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0x14, 2), checked((ushort)frameRateDenominator));
        return BuildChunk("VIDH", payload);
    }

    public static byte[] CreateFrameChunk(Vid1SyntheticVideoFrameSpec frame)
    {
        var viddChunk = BuildChunk("VIDD", CreateViddPayload(frame));
        var framePayload = new byte[0x18 + viddChunk.Length];
        viddChunk.CopyTo(framePayload.AsSpan(0x18));
        return BuildChunk("FRAM", framePayload);
    }

    public static byte[] BuildChunk(string tag, byte[] payload)
    {
        var chunk = new byte[8 + payload.Length];
        Encoding.ASCII.GetBytes(tag).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4, 4), checked((uint)chunk.Length));
        payload.CopyTo(chunk.AsSpan(8));
        return chunk;
    }

    private static byte[] CreateViddPayload(Vid1SyntheticVideoFrameSpec frame)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), frame.Tag16);

        var writer = new TestBitWriter();
        writer.WriteBits(0, 16);
        writer.WriteBits((uint)frame.PreambleClass, 2);
        writer.WriteFlag(false);
        writer.WriteFlag(frame.UsesCustomQuantMatrices);

        if (frame.UsesCustomQuantMatrices)
        {
            writer.WriteFlag(true);
            WriteTerminatedMatrix(writer);
            writer.WriteFlag(false);
        }

        writer.WriteFlag(frame.StateFlag3c);
        writer.WriteFlag(false);
        writer.WriteBits((uint)frame.IntraDcThresholdIndex, 3);
        writer.WriteBits((uint)frame.Quantizer, 5);

        if (frame.PreambleClass != 0)
            writer.WriteBits((uint)Math.Clamp(frame.ForwardCode ?? 1, 0, 7), 3);
        if (frame.PreambleClass == 2)
            writer.WriteBits((uint)Math.Clamp(frame.BackwardCode ?? 1, 0, 7), 3);

        if (frame.PreambleClass == 2)
            writer.WriteBits(frame.AlternateFrameStateWord ?? 0x55667788u, 32);
        else
            writer.WriteBits(frame.CurrentFrameStateWord ?? 0x11223344u, 32);

        if (frame.StateFlag3c)
        {
            writer.WriteFlag(false);
            writer.WriteFlag(false);
        }

        var metadata = writer.ToBytes();
        var codedPayload = frame.CodedPayload?.ToArray() ?? [0x10, 0x20, 0x30, 0x40];
        if (codedPayload.Length > 0)
        {
            if (frame.HasSpecialCallerGate)
                codedPayload[0] |= 0x80;
            else
                codedPayload[0] &= 0x7F;
        }

        return [.. header, .. metadata, .. codedPayload, .. new byte[8]];
    }

    private static void WriteTerminatedMatrix(TestBitWriter writer)
    {
        writer.WriteBits(0x12, 8);
        writer.WriteBits(0x00, 8);
    }

    private sealed class TestBitWriter
    {
        private readonly List<byte> _bits = [];

        public void WriteBits(uint value, int bitCount)
        {
            for (var bitIndex = bitCount - 1; bitIndex >= 0; bitIndex--)
                _bits.Add((byte)((value >> bitIndex) & 1));
        }

        public void WriteFlag(bool value)
        {
            _bits.Add(value ? (byte)1 : (byte)0);
        }

        public byte[] ToBytes()
        {
            while ((_bits.Count & 7) != 0)
                _bits.Add(0);

            var output = new byte[_bits.Count / 8];
            for (var index = 0; index < _bits.Count; index++)
            {
                if (_bits[index] == 0)
                    continue;

                output[index >> 3] |= (byte)(1 << (7 - (index & 7)));
            }

            return output;
        }
    }
}
