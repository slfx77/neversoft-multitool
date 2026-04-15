using System.Globalization;

namespace NeversoftMultitool.Core.Formats.Video;

internal static class Vid1VideoRebuilder
{
    private const int DefaultTimeIncrementBits = 5;
    private const string DefaultPrefixFps = "30";
    private static readonly byte[] VopStartCode = [0x00, 0x00, 0x01, 0xB6];

    internal static bool TryBuildPrefix(string ffmpegPath, int width, int height, double frameRate, out byte[] prefix, out string error)
    {
        prefix = [];
        error = "";

        try
        {
            var fps = frameRate > 0
                ? Math.Max(1, (int)Math.Round(frameRate)).ToString(CultureInfo.InvariantCulture)
                : DefaultPrefixFps;

            var tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Vid1Video");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}_prefix.m4v");

            try
            {
                var arguments =
                    $"-y -f lavfi -i \"color=c=black:s={width}x{height}:r={fps}:d=1\" -an -c:v mpeg4 -q:v 2 -f m4v \"{tempPath}\"";
                if (!Vid1VideoConverter.TryRunProcess(ffmpegPath, arguments, out _, out error))
                    return false;

                var data = File.ReadAllBytes(tempPath);
                var startCodeOffset = FindBytes(data, VopStartCode);
                if (startCodeOffset < 0)
                {
                    error = "Failed to find MPEG-4 VOP start code in generated prefix";
                    return false;
                }

                prefix = data.AsSpan(0, startCodeOffset).ToArray();
                return true;
            }
            finally
            {
                Vid1VideoConverter.TryDeleteFile(tempPath);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static byte[] BuildDeterministicCandidateStream(byte[] prefix, Vid1VideoFile file)
    {
        var output = new List<byte>(prefix.Length + (file.Frames.Count * 256));
        output.AddRange(prefix);
        var timeIncrement = 0;

        foreach (var frame in file.Frames)
        {
            if (frame.IsPartial || frame.CodedPayload.Length == 0)
                continue;

            var plan = GetDeterministicFramePlan(file.Variant, frame);
            if (plan is null)
                continue;

            var resolvedPlan = plan.Value;
            if (resolvedPlan.PayloadOffsetBytes >= frame.CodedPayload.Length)
                continue;

            var payload = frame.CodedPayload.AsSpan(resolvedPlan.PayloadOffsetBytes).ToArray();
            var writer = new Vid1BitWriter();
            WriteVopHeader(
                writer,
                resolvedPlan.VopType,
                timeIncrement,
                DefaultTimeIncrementBits,
                frame.IntraDcThresholdIndex,
                frame.Quantizer,
                frame.ForwardCode,
                frame.BackwardCode);
            output.AddRange(writer.ToBytes());
            output.AddRange(payload);
            timeIncrement++;
        }

        return [.. output];
    }

    internal static Vid1DeterministicFramePlan? GetDeterministicFramePlan(Vid1VideoVariant variant, Vid1VideoFrame frame)
    {
        return variant switch
        {
            Vid1VideoVariant.ThawLongForm => SelectLongFormPlan(frame),
            Vid1VideoVariant.ThawAtvi => SelectAtviPlan(frame),
            _ => SelectFallbackPlan(frame)
        };
    }

    private static Vid1DeterministicFramePlan SelectLongFormPlan(Vid1VideoFrame frame)
    {
        if (frame.HasSpecialCallerGate)
        {
            return frame.Tag16 switch
            {
                0x4014 => new Vid1DeterministicFramePlan(56, 0),
                0x5014 => new Vid1DeterministicFramePlan(580, 1),
                0x5044 => new Vid1DeterministicFramePlan(580, 1),
                _ => new Vid1DeterministicFramePlan(56, 0)
            };
        }

        return SelectFallbackPlan(frame);
    }

    private static Vid1DeterministicFramePlan SelectAtviPlan(Vid1VideoFrame frame)
    {
        if (frame.HasSpecialCallerGate)
            return new Vid1DeterministicFramePlan(500, 0);

        return SelectFallbackPlan(frame);
    }

    private static Vid1DeterministicFramePlan SelectFallbackPlan(Vid1VideoFrame frame)
    {
        return new Vid1DeterministicFramePlan(0, frame.GetFallbackVopType());
    }

    private static void WriteVopHeader(
        Vid1BitWriter writer,
        int vopType,
        int timeIncrement,
        int timeIncrementBits,
        int intraDcThresholdIndex,
        int quantizer,
        int? forwardCode,
        int? backwardCode)
    {
        writer.WriteBits(0x000001B6u, 32);
        writer.WriteBits((uint)vopType, 2);
        writer.WriteFlag(false);
        writer.WriteFlag(true);
        writer.WriteBits((uint)timeIncrement, timeIncrementBits);
        writer.WriteFlag(true);
        writer.WriteFlag(true);

        if (vopType is 1 or 3)
            writer.WriteFlag(false);

        writer.WriteBits((uint)(intraDcThresholdIndex & 0x7), 3);
        writer.WriteBits((uint)Math.Clamp(quantizer, 1, 0x1F), 5);

        if (vopType is 1 or 2 or 3)
            writer.WriteBits((uint)Math.Clamp(forwardCode ?? 1, 1, 0x7), 3);
        if (vopType == 2)
            writer.WriteBits((uint)Math.Clamp(backwardCode ?? 1, 1, 0x7), 3);

        writer.AlignToNextByte();
    }

    private static int FindBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j])
                    continue;

                found = false;
                break;
            }

            if (found)
                return i;
        }

        return -1;
    }

    internal readonly record struct Vid1DeterministicFramePlan(int PayloadOffsetBytes, int VopType);

    private sealed class Vid1BitWriter
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

        public void AlignToNextByte()
        {
            while ((_bits.Count & 7) != 0)
                _bits.Add(0);
        }

        public byte[] ToBytes()
        {
            AlignToNextByte();
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
