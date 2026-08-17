using System.Numerics;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

public sealed class ViewPoseTests
{
    [Fact]
    public void ToArguments_AlwaysJoinsValuesWithEquals()
    {
        // Both forms parse (GlbRenderCameraOptionTests pins that), but '=' binds a
        // leading-'-' value to its option beyond argument, and negative coordinates
        // are the common case here.
        var pose = new ViewPose(new Vector3(-1122.4f, 291f, -6612.3f), 175.2f, -5.1f, 45f, 1450, 900);

        var arguments = pose.ToArguments();

        Assert.Equal(
            "--camera-eye=-1122.4,291,-6612.3 --camera-yaw=175.2 --camera-pitch=-5.1 " +
            "--camera-fov=45 --camera-size=1450x900",
            arguments);
        Assert.DoesNotContain("--camera-eye -", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_NegativeCoordinates_RoundTripThroughTheEmittedText()
    {
        var original = new ViewPose(
            new Vector3(-24942.67f, 237.78f, -7054.67f), 180f, -12.25f, 45f, 800, 600);

        // Re-read the pose from exactly the text the clipboard would carry.
        var arguments = ParseArguments(original.ToArguments());
        Assert.True(ViewPose.TryCreate(
            arguments["--camera-eye"],
            float.Parse(arguments["--camera-yaw"], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(arguments["--camera-pitch"], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(arguments["--camera-fov"], System.Globalization.CultureInfo.InvariantCulture),
            arguments["--camera-size"],
            fallbackEdge: 512,
            out var restored,
            out var error));

        Assert.Null(error);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void TryCreate_WithoutEye_YieldsNoPoseAndNoError()
    {
        Assert.True(ViewPose.TryCreate(
            null, ViewPose.Unsupplied, ViewPose.Unsupplied, ViewPose.Unsupplied, null,
            fallbackEdge: 512, out var pose, out var error));

        Assert.Null(pose);
        Assert.Null(error);
    }

    [Fact]
    public void TryCreate_CameraOptionWithoutEye_IsRejected()
    {
        // Silently ignoring a yaw with no eye would render the default framing and
        // look like the pose was honoured.
        Assert.False(ViewPose.TryCreate(
            null, 90f, ViewPose.Unsupplied, ViewPose.Unsupplied, null,
            fallbackEdge: 512, out var pose, out var error));

        Assert.Null(pose);
        Assert.Contains("--camera-eye", error);
    }

    [Fact]
    public void TryCreate_OmittedOptionalValues_UseDocumentedDefaults()
    {
        Assert.True(ViewPose.TryCreate(
            "1,2,3", ViewPose.Unsupplied, ViewPose.Unsupplied, ViewPose.Unsupplied, null,
            fallbackEdge: 256, out var pose, out _));

        var resolved = Assert.NotNull(pose);
        Assert.Equal(new Vector3(1f, 2f, 3f), resolved.Eye);
        Assert.Equal(0f, resolved.YawDegrees);
        Assert.Equal(0f, resolved.PitchDegrees);
        Assert.Equal(ViewPose.DefaultFovDegrees, resolved.FovDegrees);
        Assert.Equal(256, resolved.Width);
        Assert.Equal(256, resolved.Height);
    }

    [Theory]
    [InlineData("1,2", "three comma-separated")]
    [InlineData("1,2,3,4", "three comma-separated")]
    [InlineData("1,two,3", "not a finite number")]
    [InlineData("1,NaN,3", "not a finite number")]
    public void TryParseEye_RejectsMalformedInput(string text, string expectedFragment)
    {
        Assert.False(ViewPose.TryParseEye(text, out _, out var error));
        Assert.Contains(expectedFragment, error);
    }

    [Theory]
    [InlineData("1450x900", 1450, 900)]
    [InlineData("1450X900", 1450, 900)]
    [InlineData(" 32 x 16 ", 32, 16)]
    public void TryParseSize_ReadsBothSeparatorCases(string text, int width, int height)
    {
        Assert.True(ViewPose.TryParseSize(text, 512, out var w, out var h, out var error));
        Assert.Null(error);
        Assert.Equal(width, w);
        Assert.Equal(height, h);
    }

    [Theory]
    [InlineData("1450")]
    [InlineData("0x900")]
    [InlineData("-4x9")]
    [InlineData("99999x10")]
    public void TryParseSize_RejectsMalformedOrOutOfRangeSizes(string text)
    {
        Assert.False(ViewPose.TryParseSize(text, 512, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryCreate_ClampsPitchShortOfVertical()
    {
        // At exactly ±90° the up vector collapses and the basis is degenerate. The
        // viewer's own look control clamps to the same limit, so this only ever
        // guards a hand-written value.
        Assert.True(ViewPose.TryCreate(
            "0,0,0", 0f, 90f, ViewPose.Unsupplied, null,
            fallbackEdge: 64, out var pose, out _));

        var resolved = Assert.NotNull(pose);
        Assert.True(resolved.PitchDegrees < 90f);
        var (_, up, forward) = resolved.GetBasis();
        Assert.True(Vector3.Cross(up, forward).Length() > 0.001f);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(180f)]
    [InlineData(float.PositiveInfinity)]
    public void TryCreate_RejectsUnusableFieldsOfView(float fov)
    {
        Assert.False(ViewPose.TryCreate(
            "0,0,0", ViewPose.Unsupplied, ViewPose.Unsupplied, fov, null,
            fallbackEdge: 64, out _, out var error));
        Assert.Contains("--camera-fov", error);
    }

    [Fact]
    public void GetBasis_NeutralPose_LooksDownNegativeZLikeThreeJs()
    {
        var (right, up, forward) = new ViewPose(Vector3.Zero, 0f, 0f, 45f, 8, 8).GetBasis();

        AssertClose(new Vector3(1f, 0f, 0f), right);
        AssertClose(new Vector3(0f, 1f, 0f), up);
        AssertClose(new Vector3(0f, 0f, -1f), forward);
    }

    [Fact]
    public void GetBasis_PositiveYawTurnsTowardNegativeX()
    {
        // three.js rotates the -Z view direction about +Y, so a quarter turn looks
        // along -X. Getting this backwards mirrors every replayed view.
        var (_, _, forward) = new ViewPose(Vector3.Zero, 90f, 0f, 45f, 8, 8).GetBasis();

        AssertClose(new Vector3(-1f, 0f, 0f), forward);
    }

    [Fact]
    public void GetBasis_PositivePitchLooksUp()
    {
        var (_, _, forward) = new ViewPose(Vector3.Zero, 0f, 30f, 45f, 8, 8).GetBasis();

        Assert.Equal(0.5f, forward.Y, 4);
    }

    [Fact]
    public void GetBasis_IsOrthonormalAndRollFree()
    {
        var (right, up, forward) = new ViewPose(Vector3.Zero, 37f, -22f, 45f, 8, 8).GetBasis();

        Assert.Equal(1f, right.Length(), 4);
        Assert.Equal(1f, up.Length(), 4);
        Assert.Equal(1f, forward.Length(), 4);
        Assert.Equal(0f, Vector3.Dot(right, up), 4);
        Assert.Equal(0f, Vector3.Dot(right, forward), 4);
        Assert.Equal(0f, Vector3.Dot(up, forward), 4);

        // Roll is structurally zero: yaw never tilts the horizon, so the right
        // vector stays in the world's horizontal plane.
        Assert.Equal(0f, right.Y, 5);
    }

    [Fact]
    public void FocalLength_MatchesTheHalfAngleTangent()
    {
        // A 90° vertical field of view puts the image plane exactly half a height away.
        var pose = new ViewPose(Vector3.Zero, 0f, 0f, 90f, 200, 100);

        Assert.Equal(50f, pose.FocalLength(100), 3);
    }

    private static Dictionary<string, string> ParseArguments(string arguments)
    {
        return arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }
}
