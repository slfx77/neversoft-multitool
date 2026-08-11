using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

/// <summary>
///     Text-level pins on the viewer's colour-pulse wiring.
///     <para>
///         Crude on purpose. <c>mesh-viewer.html</c> has no test harness, and the
///         project has already shipped a viewer regression that <c>node --check</c>
///         happily accepted — a collection reset in the wrong function froze every
///         UV wibble. These assertions cover exactly that class: the update is
///         actually called from the animation loop, setup runs for BOTH scene
///         roots, and unload resets the state.
///     </para>
/// </summary>
public class PsxColourPulseViewerContractTests
{
    private static string ReadViewer()
    {
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(directory, "src", "NeversoftMultitool", "Assets", "mesh-viewer.html");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            var parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        Assert.Skip("mesh-viewer.html not found");
        return string.Empty;
    }

    [Fact]
    public void Viewer_CallsTheUpdateFromTheAnimationLoop()
    {
        var viewer = ReadViewer();

        // The wibble update is already driven from the render loop; the pulse
        // update must sit beside it, not merely exist.
        var loopIndex = viewer.IndexOf("updatePsxTextureWibbles(Math.min(dt", StringComparison.Ordinal);
        Assert.True(loopIndex > 0, "Could not find the wibble call that anchors the render loop.");

        var nearby = viewer.Substring(loopIndex, Math.Min(200, viewer.Length - loopIndex));
        Assert.Contains("updatePsxColourPulses(", nearby);
    }

    /// <summary>
    ///     Synthetic pulse-only scene: the shared clock must advance before UV
    ///     work is skipped. Historically the first no-wibble return also froze
    ///     every colour-pulse channel at frame zero.
    /// </summary>
    [Fact]
    public void Viewer_PulseOnlySceneAdvancesTheSharedClock()
    {
        var viewer = ReadViewer();
        var body = ExtractFunction(viewer, "function updatePsxTextureWibbles(dt)");

        var inactiveGate = body.IndexOf(
            "textureWibbleMeshes.length === 0 && colourPulseMeshes.length === 0",
            StringComparison.Ordinal);
        var clockAdvance = body.IndexOf("textureWibbleFrame = (textureWibbleFrame + dt * 60)", StringComparison.Ordinal);
        var uvOnlyGate = body.IndexOf("if (textureWibbleMeshes.length === 0) return", StringComparison.Ordinal);

        Assert.True(inactiveGate >= 0, "The shared clock is not gated by both animation types.");
        Assert.True(clockAdvance > inactiveGate, "The shared clock must advance after the all-inactive guard.");
        Assert.True(uvOnlyGate > clockAdvance, "Pulse-only scenes must advance before UV mutation is skipped.");
    }

    /// <summary>
    ///     Synthetic inert scene: without UV wibbles or colour-pulse bindings,
    ///     the timeline retains its frame-zero fallback.
    /// </summary>
    [Fact]
    public void Viewer_NoSurfaceAnimationKeepsTheFrameZeroFallback()
    {
        var viewer = ReadViewer();
        var body = ExtractFunction(viewer, "function updatePsxTextureWibbles(dt)");
        var firstReturn = body.IndexOf("return;", StringComparison.Ordinal);
        var clockAdvance = body.IndexOf("textureWibbleFrame =", StringComparison.Ordinal);

        Assert.Contains(
            "if (textureWibbleMeshes.length === 0 && colourPulseMeshes.length === 0) return;",
            body);
        Assert.InRange(firstReturn, 0, clockAdvance - 1);
    }

    [Fact]
    public void Viewer_ConfiguresPulsesForBothSceneRoots()
    {
        var viewer = ReadViewer();

        Assert.Contains("configurePsxColourPulses(modelRoot", viewer);
        Assert.Contains("configurePsxColourPulses(skySceneRoot", viewer);
    }

    [Fact]
    public void Viewer_ReadsTheSceneTableBeforeTheSkyReparent()
    {
        var viewer = ReadViewer();

        var read = viewer.IndexOf("neversoftColourPulseChannels", StringComparison.Ordinal);
        var reparent = viewer.IndexOf("applyPsxSkyDomes(modelRoot)", StringComparison.Ordinal);

        Assert.True(read > 0 && reparent > 0);
        Assert.True(
            read < reparent,
            "The pulse table must be read from gltf.scene before applyPsxSkyDomes moves nodes out of it.");
    }

    [Fact]
    public void Viewer_ResetsPulseStateOnUnload()
    {
        var viewer = ReadViewer();

        var body = ExtractFunction(viewer, "function unloadCurrent()");

        Assert.Contains("colourPulseMeshes = []", body);
        Assert.Contains("colourPulseChannels = []", body);
    }

    /// <summary>
    ///     The main model and detached sky root are configured in two calls.
    ///     These collections must therefore append during setup and reset only
    ///     once, before a new model is loaded. Resetting one inside its
    ///     configure function silently discards the main-scene entries when the
    ///     sky call follows (the historical frozen-UV-wibble regression).
    /// </summary>
    [Fact]
    public void Viewer_ResetsAppendOnlyMainAndSkyCollectionsOnlyOnUnload()
    {
        var viewer = ReadViewer();
        var unload = ExtractFunction(viewer, "function unloadCurrent()");
        var gpuSetup = ExtractFunction(viewer, "function configurePsxGpuVertexColors(root)");
        var wibbleSetup = ExtractFunction(viewer, "function configurePsxTextureWibbles(root)");
        var pulseSetup = ExtractFunction(viewer, "function configurePsxColourPulses(root, channels)");

        AssertResetOwnedByUnload("psxGpuMaterials = []", unload, gpuSetup);
        AssertResetOwnedByUnload("textureWibbleMeshes = []", unload, wibbleSetup);
        AssertResetOwnedByUnload("colourPulseMeshes = []", unload, pulseSetup);
        AssertResetOwnedByUnload("colourPulseChannels = []", unload, pulseSetup);
    }

    /// <summary>
    ///     New normalized COLOR_1 alpha decodes through its exact byte code;
    ///     the Y-2 rule remains only as legacy-GLB compatibility.
    /// </summary>
    [Fact]
    public void Viewer_DecodesNewPulseCodeAndKeepsLegacyMinusTwoFallback()
    {
        var viewer = ReadViewer();

        Assert.Contains("Math.round(laneValue) - 2", viewer);
        Assert.Contains("Math.round(color1.getW(i) * 255) - 1", viewer);
    }

    [Fact]
    public void Viewer_GatesAndDecodesTheBlenderSafeWibbleCarriers()
    {
        var viewer = ReadViewer();
        var body = ExtractFunction(viewer, "function installPsxCarrierAliases(obj)");

        Assert.Contains("obj.userData.neversoftPsxVertexCarriers === 1", body);
        Assert.Contains("geometry.getAttribute('_psx_color_0')", body);
        Assert.Contains("geometry.getAttribute('uv1')", body);
        Assert.Contains("geometry.getAttribute('uv2')", body);
        Assert.Contains("geometry.getAttribute('uv3')", body);
        Assert.Contains("1 - velocityCarrier.getY(i)", body);
        Assert.Contains("Math.round(1 - waveCarrier.getY(i))", body);
        Assert.Contains("1 - sizeCarrier.getY(i)", body);
        Assert.Contains("(packed >>> 12) & 15", body);
        Assert.Contains("(packed >>> 8) & 15", body);
        Assert.Contains("(packed >>> 4) & 15", body);
        Assert.Contains("packed & 15", body);
        Assert.Contains("width > 0 && height > 0", body);
    }

    [Fact]
    public void Viewer_KeepsPulsesAndWibblesOnDisjointAttributes()
    {
        var viewer = ReadViewer();

        var start = viewer.IndexOf("function updatePsxColourPulses(", StringComparison.Ordinal);
        Assert.True(start > 0, "updatePsxColourPulses not found.");
        var end = viewer.IndexOf("function psxWibbleSample(", StringComparison.Ordinal);
        var body = end > start ? viewer[start..end] : viewer.Substring(start, 2000);

        // Pulses own the colour attributes; the wibble owns uv. Sharing one
        // would make their update order significant.
        Assert.Contains("psxColor", body);
        Assert.DoesNotContain(".setXY(", body);
    }

    private static void AssertResetOwnedByUnload(
        string reset,
        string unload,
        string configure)
    {
        Assert.Contains(reset, unload);
        Assert.DoesNotContain(reset, configure);
    }

    private static string ExtractFunction(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found.");

        var openBrace = source.IndexOf('{', start);
        Assert.True(openBrace >= 0, $"Opening brace for {signature} not found.");

        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[start..(i + 1)];
                    break;
            }
        }

        Assert.Fail($"Closing brace for {signature} not found.");
        return string.Empty;
    }
}
