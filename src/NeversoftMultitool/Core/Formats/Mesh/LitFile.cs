using System.Globalization;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     Parses Neversoft .lit light definition files (3ds Max AdvLights export).
/// </summary>
public static class LitFile
{
    public static List<LitLight> Parse(string path)
    {
        var lines = File.ReadAllLines(path);
        var lights = new List<LitLight>();
        var i = 0;

        // Skip header line (e.g. "AdvLights   2.000")
        if (i < lines.Length && lines[i].TrimStart().StartsWith("AdvLights", StringComparison.Ordinal))
            i++;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();

            if (line.StartsWith("DirLight ", StringComparison.Ordinal) ||
                line.StartsWith("SpotLight ", StringComparison.Ordinal) ||
                line.StartsWith("OmniLight ", StringComparison.Ordinal))
            {
                var light = ParseLight(lines, ref i);
                if (light != null)
                    lights.Add(light);
            }
            else
            {
                i++;
            }
        }

        return lights;
    }

    private static LitLight? ParseLight(string[] lines, ref int i)
    {
        var header = lines[i].Trim();
        var spaceIdx = header.IndexOf(' ');
        if (spaceIdx < 0)
        {
            i++;
            return null;
        }

        var typeName = header[..spaceIdx];
        var name = header[(spaceIdx + 1)..].Trim();
        i++;

        var type = typeName switch
        {
            "DirLight" => LitLightType.Directional,
            "SpotLight" => LitLightType.Spot,
            "OmniLight" => LitLightType.Point,
            _ => LitLightType.Point
        };

        SkipToOpenBrace(lines, ref i);
        var tokens = CollectBlockTokens(lines, ref i);

        var light = new LitLight { Name = name, Type = type };
        ParseProperties(new Queue<string>(tokens), light);
        return light;
    }

    private static void SkipToOpenBrace(string[] lines, ref int i)
    {
        while (i < lines.Length)
        {
            if (lines[i].Trim().Contains('{'))
            {
                i++;
                return;
            }

            i++;
        }
    }

    /// <summary>
    ///     Collects tokens from the current block depth until the closing brace.
    ///     Nested blocks (BoxGradShadow, LensFlare, LightProjMap) are skipped.
    /// </summary>
    private static List<string> CollectBlockTokens(string[] lines, ref int i)
    {
        var tokens = new List<string>();
        var depth = 0;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            i++;

            if (depth > 0)
            {
                depth += line.Count(c => c == '{') - line.Count(c => c == '}');
                if (depth < 0) depth = 0;
                continue;
            }

            if (line.StartsWith("BoxGradShadow", StringComparison.Ordinal) ||
                line.StartsWith("LensFlare", StringComparison.Ordinal) ||
                line.StartsWith("LightProjMap", StringComparison.Ordinal))
            {
                depth += line.Count(c => c == '{') - line.Count(c => c == '}');
                if (depth < 0) depth = 0;
                continue;
            }

            if (line.Contains('}'))
                break;

            var cleaned = line
                .Replace('[', ' ').Replace(']', ' ')
                .Replace('(', ' ').Replace(')', ' ');

            tokens.AddRange(cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        return tokens;
    }

    private static void ParseProperties(Queue<string> q, LitLight light)
    {
        while (q.Count > 0)
        {
            var key = q.Dequeue();
            switch (key)
            {
                case "Matrix":
                    ParseMatrix(q, light);
                    break;
                case "Pos":
                    light.Position = DequeueVector3(q);
                    break;
                case "Atten1":
                    light.Atten1 = DequeueFloat(q);
                    break;
                case "Atten2":
                    light.Atten2 = DequeueFloat(q);
                    break;
                case "Radius":
                    light.Radius = DequeueFloat(q);
                    break;
                case "Hotspot":
                    light.Hotspot = DequeueFloat(q);
                    break;
                case "HeightAspect":
                    light.HeightAspect = DequeueFloat(q);
                    break;
                case "Color":
                    light.Color = DequeueVector3(q);
                    break;
                case "Ambient":
                    light.Ambient = DequeueFloat(q);
                    break;
            }
        }
    }

    private static void ParseMatrix(Queue<string> q, LitLight light)
    {
        if (q.Count < 12) return;

        // 4x3 matrix stored as 4 rows of 3 values:
        //   Row 0 (m0-m2): local X axis
        //   Row 1 (m3-m5): local Y axis
        //   Row 2 (m6-m8): local Z axis
        //   Row 3 (m9-m11): translation
        var m = new float[12];
        for (var j = 0; j < 12; j++)
            m[j] = DequeueFloat(q);

        light.Position = new Vector3(m[9], m[10], m[11]);

        // 3ds Max lights point along -Z local, so direction = -Row2
        light.Direction = Vector3.Normalize(new Vector3(-m[6], -m[7], -m[8]));
    }

    private static float DequeueFloat(Queue<string> q)
    {
        return q.Count > 0 ? ParseFloat(q.Dequeue()) : 0f;
    }

    private static Vector3 DequeueVector3(Queue<string> q)
    {
        return new Vector3(DequeueFloat(q), DequeueFloat(q), DequeueFloat(q));
    }

    private static float ParseFloat(string s)
    {
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }
}
