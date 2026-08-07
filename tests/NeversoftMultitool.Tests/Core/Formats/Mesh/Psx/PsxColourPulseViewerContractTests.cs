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

        var start = viewer.IndexOf("function unloadCurrent()", StringComparison.Ordinal);
        Assert.True(start > 0, "unloadCurrent not found.");
        var body = viewer.Substring(start, Math.Min(1400, viewer.Length - start));

        Assert.Contains("colourPulseMeshes = []", body);
        Assert.Contains("colourPulseChannels = []", body);
    }

    /// <summary>
    ///     The viewer must decode the lane as Y - 2, matching
    ///     PsxColourPulseLane.DecodeIndex. Y - 1 binds every vertex to the wrong
    ///     channel and still animates, so it fails silently.
    /// </summary>
    [Fact]
    public void Viewer_DecodesTheLaneAsMinusTwo()
    {
        var viewer = ReadViewer();

        Assert.Contains("Math.round(laneValue) - 2", viewer);
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
}
