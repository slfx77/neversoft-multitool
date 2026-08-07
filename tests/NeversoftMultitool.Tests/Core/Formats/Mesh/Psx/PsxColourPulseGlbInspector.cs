using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

/// <summary>
///     Re-derives the viewer's own work from an exported GLB — bucket vertices by
///     the <c>_PSX_FLAGS_0.Y</c> lane, look the channel up in the scene-level
///     table, evaluate it at frame 0 — and checks the result against the colour
///     actually stored on the vertex.
///     <para>
///         This deliberately reads the GLB rather than the in-memory document, so
///         it exercises the encode, the scene extras and the lane packing the way
///         a consumer would.
///     </para>
/// </summary>
internal static class PsxColourPulseGlbInspector
{
    /// <summary>Returns the number of pulsed vertices checked.</summary>
    public static int CheckFrameZero(byte[] glb, out List<string> mismatches)
    {
        mismatches = [];
        var (json, binary) = SplitGlb(glb);
        using var document = json;
        var root = document.RootElement;

        var sceneIndex = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
        if (!root.TryGetProperty("scenes", out var scenes) || scenes.GetArrayLength() <= sceneIndex)
            return 0;
        if (!scenes[sceneIndex].TryGetProperty("extras", out var extras)
            || !extras.TryGetProperty("neversoftColourPulseChannels", out var channelsJson))
        {
            return 0;
        }

        var channels = channelsJson.EnumerateArray().ToArray();
        var checkedVertices = 0;

        foreach (var primitive in root.GetProperty("meshes").EnumerateArray()
                     .SelectMany(mesh => mesh.GetProperty("primitives").EnumerateArray()))
        {
            var attributes = primitive.GetProperty("attributes");
            if (!attributes.TryGetProperty("_PSX_FLAGS_0", out var flagsIndex)
                || !attributes.TryGetProperty("_PSX_COLOR_0", out var colorIndex))
            {
                continue;
            }

            var flags = ReadAccessor(root, binary, flagsIndex.GetInt32());
            var colors = ReadAccessor(root, binary, colorIndex.GetInt32());

            for (var i = 0; i < flags.Count && i < colors.Count; i++)
            {
                var channel = PsxColourPulseLane.DecodeIndex(flags[i][1]);
                if (channel < 0)
                    continue;

                if (channel >= channels.Length)
                {
                    mismatches.Add($"vertex {i}: channel {channel} is outside the {channels.Length}-entry table");
                    continue;
                }

                var expected = EvaluateFrameZero(channels[channel]);
                var actual = colors[i];
                for (var c = 0; c < 4; c++)
                {
                    // The bake lerps in the 0..255 byte domain and then divides;
                    // a consumer lerps the already-normalized keys. Same maths,
                    // different float rounding, so allow ~1e-3. A genuinely
                    // mis-bound channel is out by 0.1-0.7, far above this.
                    if (Math.Abs(expected[c] - actual[c]) <= 1e-3f)
                        continue;

                    mismatches.Add(
                        $"channel {channel} vertex {i}: stored [{string.Join(", ", actual.Select(v => v.ToString("F4")))}] " +
                        $"but frame 0 is [{string.Join(", ", expected.Select(v => v.ToString("F4")))}]");
                    break;
                }

                checkedVertices++;
            }
        }

        return checkedVertices;
    }

    private static float[] EvaluateFrameZero(JsonElement channel)
    {
        var keys = channel.GetProperty("keys").EnumerateArray()
            .Select(static key => key.EnumerateArray().Select(static v => v.GetSingle()).ToArray())
            .ToArray();
        var intervals = channel.GetProperty("intervals").EnumerateArray()
            .Select(static v => (int)v.GetSingle())
            .ToArray();

        var keyIndex = (int)channel.GetProperty("keyIndex").GetSingle();
        if (keyIndex >= keys.Length)
            keyIndex = 0;
        var time = (int)channel.GetProperty("accumulator").GetSingle();

        for (var guard = 0; guard < 256; guard++)
        {
            if (intervals[keyIndex] == 0 || time < intervals[keyIndex])
                break;
            time -= intervals[keyIndex];
            keyIndex = (keyIndex + 1) % keys.Length;
        }

        var current = keys[keyIndex];
        var next = keys[(keyIndex + 1) % keys.Length];
        var amount = intervals[keyIndex] == 0
            ? 0f
            : Math.Clamp(time / (float)intervals[keyIndex], 0f, 1f);

        var result = new float[4];
        for (var c = 0; c < 4; c++)
            result[c] = current[c] + (next[c] - current[c]) * amount;
        return result;
    }

    private static (JsonDocument Json, byte[] Binary) SplitGlb(byte[] glb)
    {
        JsonDocument? json = null;
        byte[] binary = [];
        var offset = 12;
        while (offset + 8 <= glb.Length)
        {
            var length = BitConverter.ToInt32(glb, offset);
            var type = BitConverter.ToUInt32(glb, offset + 4);
            if (type == 0x4E4F534A)
                json = JsonDocument.Parse(glb.AsMemory(offset + 8, length));
            else
                binary = glb.AsSpan(offset + 8, length).ToArray();
            offset += 8 + length;
        }

        return (json ?? throw new InvalidDataException("No JSON chunk"), binary);
    }

    private static List<float[]> ReadAccessor(JsonElement root, byte[] binary, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];

        var componentCount = accessor.GetProperty("type").GetString() switch
        {
            "VEC4" => 4,
            "VEC3" => 3,
            "VEC2" => 2,
            _ => 1
        };
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var componentSize = componentType switch
        {
            5126 => 4, // FLOAT
            5123 => 2, // UNSIGNED_SHORT
            5121 => 1, // UNSIGNED_BYTE
            _ => 4
        };
        var normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();

        var baseOffset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                         + (accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        var stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : componentCount * componentSize;

        var count = accessor.GetProperty("count").GetInt32();
        var values = new List<float[]>(count);
        for (var i = 0; i < count; i++)
        {
            var element = new float[componentCount];
            for (var c = 0; c < componentCount; c++)
            {
                var at = baseOffset + i * stride + c * componentSize;
                element[c] = componentType switch
                {
                    5126 => BitConverter.ToSingle(binary, at),
                    5123 => normalized ? BitConverter.ToUInt16(binary, at) / 65535f : BitConverter.ToUInt16(binary, at),
                    5121 => normalized ? binary[at] / 255f : binary[at],
                    _ => 0f
                };
            }

            values.Add(element);
        }

        return values;
    }
}
