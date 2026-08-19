namespace NeversoftMultitool.Tests.Core.Rendering;

/// <summary>
///     Text-level pins on the viewer's wireframe toggle, in the same style as
///     <c>PsxColourPulseViewerContractTests</c> — mesh-viewer.html has no test
///     harness, so the contract between the WinUI host and the page is held
///     together by these assertions.
/// </summary>
public sealed class ViewerWireframeContractTests
{
    private static readonly Lazy<string> ViewerSource = new(ReadViewerCore);

    private static string ReadViewer() => ViewerSource.Value;

    private static string ReadViewerCore()
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
    public void Viewer_ExportsTheWireframeContract()
    {
        var viewer = ReadViewer();

        Assert.Contains("window.setWireframeEnabled = setWireframeEnabled", viewer);

        var setter = ExtractFunction(viewer, "function setWireframeEnabled(enabled)");
        Assert.Contains("wireframeEnabled = !!enabled", setter);
        Assert.Contains("applyWireframe()", setter);
    }

    /// <summary>
    ///     The sky backdrop is camera-locked and surrounds the eye — its
    ///     wireframe would fill the whole viewport. Excluded structurally (the
    ///     apply traverses the model scene only) plus a per-mesh marker check;
    ///     multi-material meshes are real in this viewer, so the material
    ///     array shape must be handled.
    /// </summary>
    [Fact]
    public void Viewer_WireframeSkipsSkyAndHandlesMaterialArrays()
    {
        var viewer = ReadViewer();
        var apply = ExtractFunction(viewer, "function applyWireframe()");

        Assert.Contains("modelRoot.traverse", apply);
        Assert.DoesNotContain("skySceneRoot.traverse", apply);
        Assert.Contains("obj.userData.neversoftSkyMesh", apply);
        Assert.Contains("Array.isArray(obj.material)", apply);
        Assert.Contains("'wireframe' in material", apply);
    }

    /// <summary>
    ///     The flag must land on the FINAL material instances, so the load
    ///     chain applies it after the last material swap/clone (the sky
    ///     scene's native-blend pass) and before the page reports loaded. And
    ///     it is a session preference: unloadCurrent must not reset it.
    /// </summary>
    [Fact]
    public void Viewer_AppliesWireframeAfterMaterialSwapsAndKeepsItAcrossLoads()
    {
        var viewer = ReadViewer();

        var lastSwap = viewer.IndexOf("applyNativeMaterialBlending(skySceneRoot)", StringComparison.Ordinal);
        var apply = viewer.IndexOf("applyWireframe();", lastSwap < 0 ? 0 : lastSwap, StringComparison.Ordinal);
        var loadedPost = viewer.IndexOf("postLoaded(names, duration);", StringComparison.Ordinal);

        Assert.True(lastSwap >= 0, "Could not find the sky-scene blend pass that anchors the load chain.");
        Assert.True(apply > lastSwap, "applyWireframe() must run after the last material swap in the load chain.");
        Assert.True(loadedPost > apply, "applyWireframe() must run before the page reports loaded.");

        var unload = ExtractFunction(viewer, "function unloadCurrent()");
        Assert.DoesNotContain("wireframeEnabled", unload);
    }

    /// <summary>
    ///     PS2 worldzone materials never match the PSX <c>__st[13]</c> name
    ///     suffix, so the additive composite must also key on the
    ///     <c>neversoftBlendClass</c> material extra the exporter publishes
    ///     (GLTFLoader surfaces glTF material extras as material.userData).
    /// </summary>
    [Fact]
    public void Viewer_AdditiveBlendingKeysOnMetadataAsWellAsPsxNames()
    {
        var viewer = ReadViewer();
        var apply = ExtractFunction(viewer, "function applyNativeMaterialBlending(root)");

        Assert.Contains("material.userData.neversoftBlendClass === 'additive'", apply);
        Assert.Contains("__st[13]", apply);
        Assert.Contains("THREE.AdditiveBlending", apply);
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
