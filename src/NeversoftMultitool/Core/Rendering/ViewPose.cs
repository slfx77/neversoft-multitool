using System.Globalization;
using System.Numerics;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     An explicit first-person camera: the pose the interactive viewer copies to the
///     clipboard and the headless renderer replays.
/// </summary>
/// <remarks>
///     <para>
///         Coordinates are glTF space (Y-up, right-handed) — the same space the in-app
///         three.js viewer works in and the same space <see cref="GlbRenderer" /> loads.
///         That is why a pose transfers between the two renderers with no coordinate
///         mapping at all, unlike Xbox360MemoryCarver, whose viewer and profiler had to
///         bridge Z-up game space.
///     </para>
///     <para>
///         Orientation is yaw + pitch with no roll. This is lossless for every viewer
///         control mode: fly and walk build the camera quaternion from a
///         <c>THREE.Euler(pitch, yaw, 0, 'YXZ')</c> that forces roll to zero, and
///         OrbitControls never rolls either. Yaw 0 / pitch 0 looks down -Z, matching
///         three.js's default camera; positive pitch looks up.
///     </para>
///     <para>
///         Both the formatter and the parser live here so the GUI, <c>glb-render</c> and
///         <c>glb-gif</c> cannot drift apart. The format is a fragment of a command line
///         rather than JSON so a copied pose is paste-and-run, which is the whole point
///         of the feature.
///     </para>
/// </remarks>
public readonly record struct ViewPose(
    Vector3 Eye,
    float YawDegrees,
    float PitchDegrees,
    float FovDegrees,
    int Width,
    int Height)
{
    public const string EyeOptionName = "--camera-eye";
    public const string YawOptionName = "--camera-yaw";
    public const string PitchOptionName = "--camera-pitch";
    public const string FovOptionName = "--camera-fov";
    public const string SizeOptionName = "--camera-size";

    /// <summary>Vertical field of view used when the pose does not carry one.</summary>
    /// <remarks>Matches the viewer's <c>PerspectiveCamera(45, …)</c>.</remarks>
    public const float DefaultFovDegrees = 45f;

    public const float MinFovDegrees = 1f;
    public const float MaxFovDegrees = 179f;

    /// <summary>Upper bound on either output edge, so a typo cannot ask for a terabyte.</summary>
    public const int MaxImageEdge = 8192;

    private const float PitchLimitDegrees = 89.9f;

    /// <summary>
    ///     Render the pose as command-line arguments for <c>glb-render</c> / <c>glb-gif</c>.
    /// </summary>
    /// <remarks>
    ///     Values are joined with '=' rather than a space. Both forms parse today —
    ///     System.CommandLine accepts a space-separated value beginning with '-', and a
    ///     test pins that — but a negative coordinate is exactly the shape that a shell,
    ///     a chat client or a future parser is most likely to mangle, and '=' binds the
    ///     value to its option beyond argument. Xbox360MemoryCarver hand-rolled a parser
    ///     rather than rely on it (RendererProfilerOptions.cs:481).
    /// </remarks>
    public string ToArguments()
    {
        return string.Join(
            ' ',
            $"{EyeOptionName}={Number(Eye.X)},{Number(Eye.Y)},{Number(Eye.Z)}",
            $"{YawOptionName}={Angle(YawDegrees)}",
            $"{PitchOptionName}={Angle(PitchDegrees)}",
            $"{FovOptionName}={Angle(FovDegrees)}",
            $"{SizeOptionName}={Width.ToString(CultureInfo.InvariantCulture)}x" +
            Height.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Render an orthographic/isometric viewpoint as arguments.
    /// </summary>
    /// <remarks>
    ///     The existing <c>--azimuth</c>/<c>--elevation</c> options already reproduce a
    ///     non-perspective view exactly, so those modes need none of the perspective
    ///     machinery. Kept beside <see cref="ToArguments" /> so both spellings of "this
    ///     is the view I am looking at" stay in one place.
    /// </remarks>
    public static string FormatOrthographicArguments(float azimuthDegrees, float elevationDegrees)
    {
        return $"--azimuth={Angle(azimuthDegrees)} --elevation={Angle(elevationDegrees)}";
    }

    /// <summary>
    ///     Build a pose from raw option values, or report why they do not make one.
    /// </summary>
    /// <remarks>
    ///     <paramref name="eye" /> is the switch: supplying it selects the perspective
    ///     path, and everything else falls back to a documented default. Absent, there is
    ///     no pose and the caller keeps its azimuth/elevation framing.
    /// </remarks>
    public static bool TryCreate(
        string? eye,
        float yawDegrees,
        float pitchDegrees,
        float fovDegrees,
        string? size,
        int fallbackEdge,
        out ViewPose? pose,
        out string? error)
    {
        pose = null;
        error = null;

        if (string.IsNullOrWhiteSpace(eye))
        {
            if (!string.IsNullOrWhiteSpace(size) || IsSupplied(yawDegrees) ||
                IsSupplied(pitchDegrees) || IsSupplied(fovDegrees))
            {
                error = $"{EyeOptionName} is required when any other camera option is given.";
                return false;
            }

            return true;
        }

        if (!TryParseEye(eye, out var eyePoint, out error))
            return false;

        if (!TryParseSize(size, fallbackEdge, out var width, out var height, out error))
            return false;

        var yaw = IsSupplied(yawDegrees) ? yawDegrees : 0f;
        var pitch = IsSupplied(pitchDegrees) ? pitchDegrees : 0f;
        var fov = IsSupplied(fovDegrees) ? fovDegrees : DefaultFovDegrees;

        if (!float.IsFinite(yaw))
        {
            error = $"{YawOptionName} must be a finite number of degrees.";
            return false;
        }

        if (!float.IsFinite(pitch))
        {
            error = $"{PitchOptionName} must be a finite number of degrees.";
            return false;
        }

        if (!float.IsFinite(fov) || fov < MinFovDegrees || fov > MaxFovDegrees)
        {
            error = $"{FovOptionName} must be between {MinFovDegrees} and {MaxFovDegrees} degrees.";
            return false;
        }

        // A pitch of exactly ±90° makes the up vector degenerate. The viewer clamps to
        // the same limit while looking around (PITCH_LIMIT), so a copied pose can never
        // exceed it; clamping here only guards hand-written values.
        pitch = Math.Clamp(pitch, -PitchLimitDegrees, PitchLimitDegrees);

        pose = new ViewPose(eyePoint, yaw, pitch, fov, width, height);
        return true;
    }

    /// <summary>Parse an <c>X,Y,Z</c> eye position.</summary>
    public static bool TryParseEye(string? text, out Vector3 eye, out string? error)
    {
        eye = default;
        error = null;

        var parts = (text ?? string.Empty).Split(',');
        if (parts.Length != 3)
        {
            error = $"{EyeOptionName} must be three comma-separated numbers, e.g. " +
                    $"{EyeOptionName}=1122.4,291,-6612.3";
            return false;
        }

        Span<float> values = stackalloc float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out values[i]) || !float.IsFinite(values[i]))
            {
                error = $"{EyeOptionName} component '{parts[i].Trim()}' is not a finite number.";
                return false;
            }
        }

        eye = new Vector3(values[0], values[1], values[2]);
        return true;
    }

    /// <summary>Parse a <c>WxH</c> output size, falling back to a square of the render edge.</summary>
    public static bool TryParseSize(
        string? text, int fallbackEdge, out int width, out int height, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            var edge = Math.Clamp(fallbackEdge, 1, MaxImageEdge);
            width = edge;
            height = edge;
            return true;
        }

        width = 0;
        height = 0;

        var parts = text.Split('x', 'X');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out width) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out height))
        {
            error = $"{SizeOptionName} must look like 1450x900.";
            return false;
        }

        if (width < 1 || height < 1 || width > MaxImageEdge || height > MaxImageEdge)
        {
            error = $"{SizeOptionName} edges must be between 1 and {MaxImageEdge} pixels.";
            width = 0;
            height = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    ///     The camera's world-space basis: where right, up and forward point.
    /// </summary>
    /// <remarks>
    ///     Derived from R = Ry(yaw)·Rx(pitch) applied to three.js's camera axes, so it
    ///     reproduces <c>perspCamera.quaternion.setFromEuler(flyEuler)</c> exactly.
    ///     Forward is the viewing direction (three.js cameras look down local -Z).
    /// </remarks>
    public (Vector3 Right, Vector3 Up, Vector3 Forward) GetBasis()
    {
        var yaw = YawDegrees * (MathF.PI / 180f);
        var pitch = PitchDegrees * (MathF.PI / 180f);

        var cy = MathF.Cos(yaw);
        var sy = MathF.Sin(yaw);
        var cp = MathF.Cos(pitch);
        var sp = MathF.Sin(pitch);

        var right = new Vector3(cy, 0f, -sy);
        var up = new Vector3(sy * sp, cp, cy * sp);
        var forward = new Vector3(-sy * cp, sp, -cy * cp);
        return (right, up, forward);
    }

    /// <summary>
    ///     Distance from the projection plane to the image plane, in supersampled pixels.
    /// </summary>
    public float FocalLength(int pixelHeight)
    {
        var fov = FovDegrees * (MathF.PI / 180f);
        return pixelHeight * 0.5f / MathF.Tan(fov * 0.5f);
    }

    /// <summary>A float option that was never supplied on the command line.</summary>
    public static float Unsupplied => float.NaN;

    private static bool IsSupplied(float value) => !float.IsNaN(value);

    private static string Number(float value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Angle(float value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
