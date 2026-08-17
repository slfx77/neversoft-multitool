using System.Numerics;
using System.Text.Json;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     A viewpoint captured from the interactive viewer, exactly as the page sends it.
/// </summary>
/// <remarks>
///     <para>
///         Mirrors <c>captureViewPose()</c> in <c>Assets/mesh-viewer.html</c>. There is no
///         shared schema between the two languages, so the field names below and the ones
///         the page writes are held together by <c>CapturedViewContractTests</c> — the same
///         arrangement Xbox360MemoryCarver uses to pin its clipboard format.
///     </para>
///     <para>
///         Parsing and formatting live in Core rather than in the WinUI control because
///         <c>App/**</c> is excluded from the cross-platform target and the test project
///         builds <c>net10.0</c> only: a formatter written in App could not be tested at all.
///     </para>
/// </remarks>
public readonly record struct CapturedView(
    string? ProjectionMode,
    string? ControlMode,
    Vector3 Eye,
    float Yaw,
    float Pitch,
    float Fov,
    int Width,
    int Height,
    float Azimuth,
    float Elevation)
{
    private const string PerspectiveMode = "perspective";
    private const string OrbitMode = "orbit";

    /// <summary>
    ///     Whether this view is reproduced by the renderer's azimuth/elevation options.
    /// </summary>
    /// <remarks>
    ///     A locked orthographic or isometric orbit already replays exactly through the
    ///     options that existed before poses did, so it needs none of the perspective
    ///     machinery. Fly and walk are perspective-only, and a non-perspective
    ///     <c>projectionMode</c> remembered while flying is not yet in effect — hence
    ///     requiring orbit as well, not just the projection.
    /// </remarks>
    public bool UsesOrthographicArguments =>
        !string.Equals(ProjectionMode, PerspectiveMode, StringComparison.Ordinal) &&
        string.Equals(ControlMode, OrbitMode, StringComparison.Ordinal);

    /// <summary>Command-line arguments that reproduce this view headlessly.</summary>
    public string ToArguments()
    {
        return UsesOrthographicArguments
            ? ViewPose.FormatOrthographicArguments(Azimuth, Elevation)
            : new ViewPose(Eye, Yaw, Pitch, Fov, Width, Height).ToArguments();
    }

    /// <summary>
    ///     Read the page's <c>copyView</c> payload.
    /// </summary>
    /// <remarks>
    ///     Every numeric field is required and must be finite: a partial pose would copy a
    ///     viewpoint that silently is not the one on screen, which is worse than refusing.
    /// </remarks>
    public static bool TryParse(JsonElement pose, out CapturedView view)
    {
        view = default;

        if (pose.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryReadVector(pose, "eye", out var eye) ||
            !TryReadFloat(pose, "yaw", out var yaw) ||
            !TryReadFloat(pose, "pitch", out var pitch) ||
            !TryReadFloat(pose, "fov", out var fov) ||
            !TryReadInt(pose, "width", out var width) ||
            !TryReadInt(pose, "height", out var height) ||
            !TryReadFloat(pose, "azimuth", out var azimuth) ||
            !TryReadFloat(pose, "elevation", out var elevation))
        {
            return false;
        }

        if (width < 1 || height < 1)
            return false;

        view = new CapturedView(
            ReadString(pose, "projectionMode"),
            ReadString(pose, "controlMode"),
            eye, yaw, pitch, fov, width, height, azimuth, elevation);
        return true;
    }

    private static string? ReadString(JsonElement pose, string name) =>
        pose.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadFloat(JsonElement pose, string name, out float result)
    {
        result = 0f;
        if (!pose.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return false;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            return false;

        result = (float)number;
        return float.IsFinite(result);
    }

    private static bool TryReadInt(JsonElement pose, string name, out int result)
    {
        result = 0;
        if (!TryReadFloat(pose, name, out var number))
            return false;
        if (number < int.MinValue || number > int.MaxValue)
            return false;

        result = (int)MathF.Round(number);
        return true;
    }

    private static bool TryReadVector(JsonElement pose, string name, out Vector3 result)
    {
        result = default;
        if (!pose.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() != 3)
        {
            return false;
        }

        Span<float> components = stackalloc float[3];
        var index = 0;
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number ||
                !element.TryGetDouble(out var number) ||
                !double.IsFinite(number))
            {
                return false;
            }

            components[index++] = (float)number;
        }

        result = new Vector3(components[0], components[1], components[2]);
        return true;
    }
}
