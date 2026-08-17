using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

/// <summary>
///     Pins the agreement between the viewer page and the C# that consumes it.
/// </summary>
/// <remarks>
///     <para>
///         The pose crosses a WebView2 boundary as loose JSON, so nothing but these tests
///         stops the page and <see cref="CapturedView" /> from drifting apart. The same
///         arrangement guards Xbox360MemoryCarver's clipboard format.
///     </para>
///     <para>
///         These read the page's source text. They cannot execute its JavaScript, so they
///         pin the field names and the tuning constants, not the integration that uses
///         them — the jump's feel still has to be judged in the app.
///     </para>
/// </remarks>
public sealed class ViewerPoseContractTests
{
    [Fact]
    public void CaptureViewPose_EmitsEveryFieldTheParserRequires()
    {
        var source = ReadViewerSource();
        var capture = ExtractFunction(source, "function captureViewPose()");

        foreach (var field in new[]
                 {
                     "projectionMode:", "controlMode:", "eye:", "yaw:", "pitch:",
                     "fov:", "width:", "height:", "azimuth:", "elevation:"
                 })
        {
            Assert.Contains(field, capture);
        }
    }

    [Fact]
    public void CopyViewPose_IsBoundToPAndSentAsTheCopyViewMessage()
    {
        var source = ReadViewerSource();

        Assert.Contains("e.code === 'KeyP'", source);
        Assert.Contains("copyViewPose()", source);
        Assert.Contains("type: 'copyView'", source);
    }

    [Fact]
    public void TryParse_ThePagePayloadShape_ProducesAUsablePose()
    {
        // The literal shape captureViewPose() serialises, walking a level.
        using var document = JsonDocument.Parse(
            """
            {
              "projectionMode": "perspective",
              "controlMode": "walk",
              "eye": [-24942.67, 237.78, -7054.67],
              "yaw": 180,
              "pitch": -5.125,
              "fov": 45,
              "width": 1450,
              "height": 900,
              "azimuth": 12.5,
              "elevation": 30
            }
            """);

        Assert.True(CapturedView.TryParse(document.RootElement, out var view));
        Assert.False(view.UsesOrthographicArguments);
        Assert.Equal(
            "--camera-eye=-24942.67,237.78,-7054.67 --camera-yaw=180 --camera-pitch=-5.13 " +
            "--camera-fov=45 --camera-size=1450x900",
            view.ToArguments());
    }

    [Fact]
    public void LockedOrthographicOrbit_ReusesTheExistingAngleOptions()
    {
        // Those projections already replay exactly through --azimuth/--elevation, so
        // they need none of the perspective machinery.
        var view = new CapturedView(
            "isometric", "orbit", Vector3.Zero, 0f, 0f, 45f, 800, 600, 45f, 30f);

        Assert.True(view.UsesOrthographicArguments);
        Assert.Equal("--azimuth=45 --elevation=30", view.ToArguments());
    }

    [Fact]
    public void OrthographicProjectionRememberedWhileFlying_StillUsesThePerspectivePath()
    {
        // setProjection() stores the choice without applying it while in fly/walk, so
        // the projection alone does not mean the view on screen is orthographic.
        var view = new CapturedView(
            "isometric", "fly", new Vector3(1f, 2f, 3f), 10f, 0f, 45f, 800, 600, 45f, 30f);

        Assert.False(view.UsesOrthographicArguments);
        Assert.Contains("--camera-eye=1,2,3", view.ToArguments());
    }

    [Theory]
    // Short vector, non-numeric component, non-finite value.
    [InlineData("\"eye\": [1, 2]")]
    [InlineData("\"eye\": [1, 2, \"x\"]")]
    // A string where a number belongs — what a sloppy page-side change would emit.
    [InlineData("\"yaw\": \"180\"")]
    [InlineData("\"width\": 0")]
    // Field simply missing.
    [InlineData("\"unused\": 0")]
    public void TryParse_PartialOrMalformedPose_IsRefused(string field)
    {
        // A half-read pose copies a viewpoint that is not the one on screen, which is
        // worse than copying nothing.
        var json =
            $$"""
              {
                "projectionMode": "perspective", "controlMode": "walk",
                "pitch": 0, "fov": 45, "height": 600, "azimuth": 0, "elevation": 0,
                {{field}}
              }
              """;

        using var document = JsonDocument.Parse(json);
        Assert.False(CapturedView.TryParse(document.RootElement, out _));
    }

    [Fact]
    public void TryParse_RejectsNonFiniteNumbers()
    {
        // JSON has no NaN literal, but a very large exponent overflows to infinity
        // and would produce a camera that renders nothing.
        using var document = JsonDocument.Parse(
            """
            {
              "projectionMode": "perspective", "controlMode": "walk",
              "eye": [1, 2, 1e400], "yaw": 0, "pitch": 0, "fov": 45,
              "width": 8, "height": 8, "azimuth": 0, "elevation": 0
            }
            """);

        Assert.False(CapturedView.TryParse(document.RootElement, out _));
    }

    [Fact]
    public void JumpConstants_MatchTheDocumentedHopHeightAndAirtime()
    {
        var source = ReadViewerSource();

        var height = ReadConstant(source, "WALK_JUMP_HEIGHT_FACTOR");
        var gravity = ReadConstant(source, "WALK_GRAVITY_FACTOR");

        Assert.Equal(1.45f, height, 4);
        Assert.Equal(20f, gravity, 4);

        // Ballistics for an eye height of 1: v0 = sqrt(2·g·peak), airtime = 2·v0/g.
        var launchSpeed = MathF.Sqrt(2f * gravity * height);
        Assert.Equal(height, launchSpeed * launchSpeed / (2f * gravity), 4);
        Assert.Equal(0.762f, 2f * launchSpeed / gravity, 3);
    }

    [Fact]
    public void SpaceIsTrackedAndGravityOwnsHeightOnlyWhileAirborne()
    {
        var source = ReadViewerSource();

        Assert.Contains("'KeyQ', 'KeyE', 'Space'", source);
        Assert.Contains("keysDown.has('Space')", source);
        // The exponential ground easing must be skipped mid-jump, or the camera is
        // pulled straight back down and there is no jump at all.
        Assert.Contains("if (!walkAirborne) {", source);
    }

    [Fact]
    public void EveryWalkHeightResetAlsoLandsAJumpInFlight()
    {
        // A reload or mode change that resets the ground state without clearing the
        // jump resumes mid-arc and sinks the camera through the floor.
        var source = ReadViewerSource().Replace("\r\n", "\n", StringComparison.Ordinal);

        var resets = 0;
        var index = source.IndexOf("walkYSettled =", StringComparison.Ordinal);
        while (index >= 0)
        {
            resets++;
            var window = source[index..Math.Min(source.Length, index + 400)];
            Assert.Contains("walkAirborne = false;", window);
            Assert.Contains("walkVerticalVelocity = 0;", window);
            index = source.IndexOf("walkYSettled =", index + 1, StringComparison.Ordinal);
        }

        // The declaration, frameModel, enterFlyWalk, the first grounded frame, and
        // restoreCameraState. A new site that forgets the jump fails the loop above;
        // this count catches a site that is removed instead.
        Assert.Equal(5, resets);
    }

    [Fact]
    public void ViewerModuleScript_Parses()
    {
        // mesh-viewer.html has no other executable coverage: a stray character in its
        // 1,700-odd lines breaks the entire 3D viewer, and every other test here only
        // reads the file as text. Node is not a build requirement, so skip without it.
        var node = ResolveNode();
        Assert.SkipWhen(node == null, "node is not on PATH");

        var script = ExtractModuleScript(ReadViewerSource());
        var path = Path.Combine(Path.GetTempPath(), $"nmt-viewer-{Guid.NewGuid():N}.mjs");
        File.WriteAllText(path, script);

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = node!,
                ArgumentList = { "--check", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            Assert.True(process.ExitCode == 0, $"mesh-viewer.html does not parse:\n{stderr}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string ExtractModuleScript(string source)
    {
        const string open = "type=\"module\">";
        var start = source.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "mesh-viewer.html has no module script");
        start += open.Length;
        var end = source.IndexOf("</script>", start, StringComparison.Ordinal);
        Assert.True(end > start, "the module script is not closed");
        return source[start..end];
    }

    private static string? ResolveNode()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not this test's problem.
            }
        }

        return null;
    }

    private static float ReadConstant(string source, string name)
    {
        var marker = $"const {name} = ";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} is not declared in mesh-viewer.html");
        start += marker.Length;
        var end = source.IndexOfAny([';', ' '], start);
        return float.Parse(source[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ExtractFunction(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} is not present in mesh-viewer.html");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{signature} is not closed as expected");
        return source[start..end];
    }

    private static string ReadViewerSource()
    {
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(
                directory, "src", "NeversoftMultitool", "Assets", "mesh-viewer.html");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Path.GetDirectoryName(directory)!;
        }

        Assert.Fail("mesh-viewer.html was not found relative to the test assembly.");
        return string.Empty;
    }
}
