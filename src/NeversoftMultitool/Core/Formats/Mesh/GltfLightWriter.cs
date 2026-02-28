using System.Numerics;
using SharpGLTF.Scenes;

namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     Handles adding parsed .lit lights to glTF scenes using KHR_lights_punctual,
///     including coordinate conversion and light type mapping.
/// </summary>
internal static class GltfLightWriter
{
    /// <summary>
    ///     Finds and parses a .lit file matching the given level name in the specified directory.
    ///     Returns null if not found or on parse failure.
    /// </summary>
    internal static List<LitLight>? FindAndParseLitFile(string levelName, string? searchDir)
    {
        if (string.IsNullOrEmpty(searchDir)) return null;
        var litPath = GltfTextureHelper.FindCompanionFile(searchDir, levelName, ".lit");
        if (litPath == null) return null;
        try
        {
            return LitFile.Parse(litPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Adds parsed .lit lights to a glTF scene using KHR_lights_punctual.
    ///     Coordinates are converted from 3ds Max space to glTF space (-X, -Y, +Z).
    /// </summary>
    internal static void AddLightsToScene(SceneBuilder scene, List<LitLight> lights)
    {
        foreach (var lit in lights)
        {
            var gltfLight = CreateGltfLight(lit);
            if (gltfLight == null) continue;

            var pos = new Vector3(-lit.Position.X, -lit.Position.Y, lit.Position.Z);
            var node = new NodeBuilder(lit.Name);
            node.LocalTransform = CreateLightTransform(pos, lit.Direction, lit.Type);

            scene.AddLight(gltfLight, node);
        }
    }

    private static LightBuilder? CreateGltfLight(LitLight lit)
    {
        var (color, intensity) = NormalizeHdrColor(lit.Color);
        var range = lit.Atten2 > 0 ? lit.Atten2 : float.PositiveInfinity;

        return lit.Type switch
        {
            LitLightType.Point => new LightBuilder.Point
            {
                Color = color, Intensity = intensity, Range = range
            },
            LitLightType.Spot => CreateSpotLight(color, intensity, range, lit),
            // DirLights with small Radius are bounded area lights — approximate as spot.
            // Large or negative Radius means unbounded directional.
            LitLightType.Directional when lit.Radius is > 0 and < 100 =>
                CreateSpotLight(color, intensity, range, lit),
            LitLightType.Directional => new LightBuilder.Directional
            {
                Color = color, Intensity = intensity
            },
            _ => null
        };
    }

    private static LightBuilder.Spot CreateSpotLight(
        Vector3 color, float intensity, float range, LitLight lit)
    {
        return new LightBuilder.Spot
        {
            Color = color,
            Intensity = intensity,
            Range = range,
            InnerConeAngle = lit.Hotspot > 0 ? lit.Hotspot / 2f : 0f,
            OuterConeAngle = lit.Radius > 0 ? lit.Radius / 2f : 0.785f
        };
    }

    private static (Vector3 Color, float Intensity) NormalizeHdrColor(Vector3 color)
    {
        var max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        return max > 1f ? (color / max, max) : (color, 1f);
    }

    /// <summary>
    ///     Creates a node transform that positions the light and orients it so that
    ///     the glTF local -Z axis points in the light's direction.
    /// </summary>
    private static Matrix4x4 CreateLightTransform(Vector3 position, Vector3? direction, LitLightType type)
    {
        if (direction == null || type == LitLightType.Point)
            return Matrix4x4.CreateTranslation(position);

        // Convert direction from .lit space to glTF space: (-X, -Y, +Z)
        var dir = direction.Value;
        var gltfDir = Vector3.Normalize(new Vector3(-dir.X, -dir.Y, dir.Z));

        // Build rotation: glTF lights point along local -Z, so rotate -Z to gltfDir
        var rotation = RotationFromTo(-Vector3.UnitZ, gltfDir);
        return Matrix4x4.CreateFromQuaternion(rotation)
               * Matrix4x4.CreateTranslation(position);
    }

    private static Quaternion RotationFromTo(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.999999f)
            return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            // 180-degree rotation around any perpendicular axis
            var perp = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var axis = Vector3.Normalize(Vector3.Cross(from, perp));
            return new Quaternion(axis, 0);
        }

        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1 + dot));
    }
}
