namespace NeversoftMultitool.Tests.Core.Rendering;

/// <summary>
///     Text-level pins on the viewer's THAW PS2 worldzone rendering passes
///     (2026-08-19): subtractive unlit swap, per-pass log-depth bias, and
///     MASK mip-stability — the three in-app halves of the E4/E6 (fake
///     shadows, black windows), E3/E5/E9 (coplanar + distant z-fighting), and
///     E8 (shingles vanish at distance) report families. mesh-viewer.html has
///     no test harness, so the host↔page contract is held by these pins.
/// </summary>
public sealed class ViewerWorldzoneRenderingContractTests
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

    /// <summary>
    ///     A subtractive bake is black RGB + luminance alpha. Current
    ///     worldzone exports are already unlit (RenderMaterial.Unlit defaults
    ///     true), so the effective correction is the OPACITY GAIN back to the
    ///     GS darkening magnitude (the bake divides by the 0.30 portability
    ///     constant); the unlit clone-swap half guards legacy lit exports.
    ///     Must keep NormalBlending (never additive) and run before the PS1
    ///     GPU patch, like the PSX unlit swap it mirrors.
    /// </summary>
    [Fact]
    public void Viewer_SubtractiveBakesSwapToUnlitWithOpacityGain()
    {
        var viewer = ReadViewer();
        var apply = ExtractFunction(viewer, "function applyNativeSubtractiveUnlit(root)");

        Assert.Contains("material.userData.neversoftBlendClass !== 'subtractive'", apply);
        Assert.Contains("THREE.MeshBasicMaterial", apply);
        Assert.Contains("unlit.color.setRGB(0, 0, 0)", apply);
        Assert.Contains("NS_SUBTRACTIVE_OPACITY_GAIN", apply);
        Assert.DoesNotContain("AdditiveBlending", apply);

        var psxUnlit = viewer.IndexOf("applyPsxSemiTransparentUnlit(modelRoot);", StringComparison.Ordinal);
        var subtractive = viewer.IndexOf("applyNativeSubtractiveUnlit(modelRoot);", StringComparison.Ordinal);
        var psxPatch = viewer.IndexOf("configurePsxGpuVertexColors(modelRoot);", StringComparison.Ordinal);
        Assert.True(psxUnlit >= 0 && subtractive > psxUnlit && psxPatch > subtractive,
            "subtractive unlit swap must run after the PSX unlit swap and before the PS1 GPU patch");
    }

    /// <summary>
    ///     polygonOffset is inert while logarithmicDepthBuffer writes
    ///     gl_FragDepth, so the per-pass separation scales the log-depth term
    ///     (view-distance-proportional — fixed world-space offsets vanish at
    ///     THAW world scale). Rank comes from neversoftPassIndex; materials
    ///     are shared across ranks so biased variants clone per
    ///     (material, rank); PSX-patched materials are skipped because
    ///     Material.clone drops onBeforeCompile.
    /// </summary>
    [Fact]
    public void Viewer_DepthBiasScalesLogDepthPerPassRank()
    {
        var viewer = ReadViewer();

        var apply = ExtractFunction(viewer, "function applyNeversoftDepthBias(root)");
        Assert.Contains("obj.userData.neversoftPassIndex", apply);
        Assert.Contains("psxGpuMaterials.includes(material)", apply);
        Assert.Contains("material.clone()", apply);

        var install = ExtractFunction(viewer, "function installNeversoftDepthBias(material, rank)");
        Assert.Contains("#include <logdepthbuf_fragment>", install);
        Assert.Contains("vFragDepth * ( 1.0 -", install);
        Assert.Contains("previous.call(this", install);
        Assert.Contains("customProgramCacheKey", install);
        Assert.Contains("'|nsDepthBias:'", install);
        Assert.Contains("needsUpdate = true", install);

        var renderOrder = viewer.IndexOf("applyNeversoftRenderOrder(modelRoot);", StringComparison.Ordinal);
        var depthBias = viewer.IndexOf("applyNeversoftDepthBias(modelRoot);", StringComparison.Ordinal);
        // The load-chain wireframe call, not the definition-area one.
        var wireframe = viewer.IndexOf("applyWireframe();", Math.Max(depthBias, 0), StringComparison.Ordinal);
        Assert.True(renderOrder >= 0 && depthBias > renderOrder && wireframe > depthBias,
            "depth bias must run after renderOrder assignment and before the wireframe pass");
    }

    /// <summary>
    ///     r170's built-in A2C+alphaTest smoothstep keeps the hard threshold
    ///     (mip-blurred interiors with fwidth≈0 still discard), so the pass
    ///     removes the test entirely under alpha-to-coverage and raises
    ///     anisotropy — while the PS1-fidelity nearest/no-mip path
    ///     self-excludes via generateMipmaps === false.
    /// </summary>
    [Fact]
    public void Viewer_MaskCutoutsUseCoverageAndAnisotropyInsteadOfAHardTest()
    {
        var viewer = ReadViewer();
        var apply = ExtractFunction(viewer, "function applyMaskCutoutStability(root)");

        Assert.Contains("alphaToCoverage = true", apply);
        Assert.Contains("material.alphaTest = 0", apply);
        Assert.Contains("getMaxAnisotropy", apply);
        Assert.Contains("generateMipmaps === false", apply);
        Assert.Contains("needsUpdate = true", apply);

        // Applies to the model scene after the PSX texture gate exists, and to
        // the sky scene after its blend pass — both before applyWireframe.
        var psxGate = viewer.IndexOf("configurePsxGpuVertexColors(modelRoot);", StringComparison.Ordinal);
        var model = viewer.IndexOf("applyMaskCutoutStability(modelRoot);", StringComparison.Ordinal);
        var skyBlend = viewer.IndexOf("applyNativeMaterialBlending(skySceneRoot);", StringComparison.Ordinal);
        var sky = viewer.IndexOf("applyMaskCutoutStability(skySceneRoot);", StringComparison.Ordinal);
        // The load-chain wireframe call, not the definition-area one.
        var wireframe = viewer.IndexOf("applyWireframe();", Math.Max(sky, 0), StringComparison.Ordinal);
        Assert.True(psxGate >= 0 && model > psxGate, "model pass must follow the PS1 GPU texture gate");
        Assert.True(skyBlend >= 0 && sky > skyBlend && wireframe > sky,
            "sky pass must follow the sky blend pass and precede applyWireframe");
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
