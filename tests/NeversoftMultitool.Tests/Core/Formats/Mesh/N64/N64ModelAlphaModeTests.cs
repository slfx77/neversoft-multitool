using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins how an N64 face's PS1 ABR state maps to a glTF alpha mode
///     (2026-08-06). The PS1's rate-0 "average" blend was a PER-TEXEL state,
///     armed only where the CLUT entry carried the STP marker; the Edge of
///     Reality art conversion dropped that marker, leaving RGBA5551's single
///     alpha bit to carry only the transparency key. So a rate-0 face with
///     one-bit art has nothing to blend, and forcing BLEND there costs the
///     depth write for no change in colour — which is what let the far inner
///     sheet of a THPS1 medal paint over the near outer sheet at an angle.
///     Rates 1-3 composite by EQUATION and must keep blending regardless.
///     <para>
///         THPS1 <c>models/030</c> pins both directions in one file: a rate-0
///         face and a rate-1 face, both bound to strictly one-bit art, so the
///         only thing that can separate their alpha modes is the rate.
///     </para>
/// </summary>
public sealed class N64ModelAlphaModeTests(TestPaths paths)
{
    private const string Thps1N64Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater (USA).z64";
    private const string SpiderManN64Build = "Spider-Man (2000-11-21, N64 - Final)";
    private const string SpiderManRomName = "Spider-Man (USA).z64";

    private ModelDocument ParseBundle(string slot, out IArchiveFileSystem fs)
    {
        return ParseBundle(Thps1N64Build, RomName, slot, out fs);
    }

    private ModelDocument ParseBundle(
        string buildName,
        string romName,
        string slot,
        out IArchiveFileSystem fs)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");
        fs = ArchiveFileSystem.TryOpen(romPath!)!;
        var backend = ArchiveAssetBackend.TryOpen(romPath!)!;
        var entry = N64Bundles.FindBundle(backend, slot);
        var source = new ArchiveAssetSource(backend, entry);

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = "n64_alpha",
            SourceKind = ModelSourceKind.N64Model
        });
    }

    [Theory]
    [InlineData("I4", N64TexFile.N64TextureRenderClass.TextureCoverage, 0, 128, true, false, false)]
    [InlineData("I8", N64TexFile.N64TextureRenderClass.TextureCoverage, 128, 128, true, false, false)]
    [InlineData("I8", N64TexFile.N64TextureRenderClass.Opaque, 0, 128, false, false, false)]
    [InlineData("I8", N64TexFile.N64TextureRenderClass.Translucent, 0, 255, true, false, true)]
    [InlineData("I8", N64TexFile.N64TextureRenderClass.Translucent, 128, 128, false, true, true)]
    [InlineData("I8", N64TexFile.N64TextureRenderClass.Unspecified, 0, 128, true, true, false)]
    [InlineData("IA8", N64TexFile.N64TextureRenderClass.Opaque, 0, 128, true, true, false)]
    public void TextureAlphaClassification_UsesTheAuthoredRdpRenderClass(
        string format,
        N64TexFile.N64TextureRenderClass renderClass,
        byte firstAlpha,
        byte secondAlpha,
        bool expectedCutout,
        bool expectedGraduated,
        bool expectedForcesBlend)
    {
        byte[] rgba = [255, 255, 255, firstAlpha, 255, 255, 255, secondAlpha];

        var actual = N64ModelCompanions.ClassifyAlpha(format, renderClass, rgba);

        Assert.Equal((expectedCutout, expectedGraduated, expectedForcesBlend), actual);
    }

    [Fact]
    public void AuthoredTranslucentTexture_PreservesAverageBlendForBinaryArt()
    {
        var texture = new N64ModelCompanions.N64ResolvedTexture(
            "cloud", 1, 1, [], HasCutout: true, HasGraduatedAlpha: false)
        {
            ForcesBlend = true
        };

        Assert.Equal(
            (ModelAlphaMode.Blend, 0.5f),
            N64MaterialCache.ResolveBlendState(0, semi: true, translucentVertices: false, texture));
        Assert.Equal(
            (ModelAlphaMode.Blend, 1f),
            N64MaterialCache.ResolveBlendState(0, semi: false, translucentVertices: false, texture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredTranslucentTexture_CachesSemiAndOpaqueFacesIndependently(bool semiFirst)
    {
        var document = new ModelDocument { Name = "forced_blend_cache" };
        var texture = new N64ModelCompanions.N64ResolvedTexture(
            "cloud", 1, 1, [], HasCutout: true, HasGraduatedAlpha: false)
        {
            ForcesBlend = true
        };
        var cache = new N64MaterialCache(document, _ => texture);
        var opaque = new N64RenderBankFile.N64Triangle(
            default, default, default, Flags: 0, MatrixIndex: 0, TextureSlot: 1);
        var semi = opaque with { Flags = PsxFaceFlags.SemiTransparent };

        var first = cache.Resolve(semiFirst ? semi : opaque, translucentVertices: false);
        var second = cache.Resolve(semiFirst ? opaque : semi, translucentVertices: false);

        Assert.NotEqual(first.MaterialIndex, second.MaterialIndex);
        var semiIndex = semiFirst ? first.MaterialIndex : second.MaterialIndex;
        var opaqueIndex = semiFirst ? second.MaterialIndex : first.MaterialIndex;
        Assert.Equal((ModelAlphaMode.Blend, 0.5f, "cloud__st0"),
            (document.Materials[semiIndex].AlphaMode,
             document.Materials[semiIndex].BaseColor.W,
             document.Materials[semiIndex].Name));
        Assert.Equal((ModelAlphaMode.Blend, 1f, "cloud"),
            (document.Materials[opaqueIndex].AlphaMode,
             document.Materials[opaqueIndex].BaseColor.W,
             document.Materials[opaqueIndex].Name));
    }

    [Fact]
    public void OrdinaryCutoutTexture_KeepsLegacySemiAndOpaqueMaterialIdentity()
    {
        var document = new ModelDocument { Name = "cutout_cache" };
        var texture = new N64ModelCompanions.N64ResolvedTexture(
            "cutout", 1, 1, [], HasCutout: true, HasGraduatedAlpha: false);
        var cache = new N64MaterialCache(document, _ => texture);
        var opaque = new N64RenderBankFile.N64Triangle(
            default, default, default, Flags: 0, MatrixIndex: 0, TextureSlot: 1);
        var semi = opaque with { Flags = PsxFaceFlags.SemiTransparent };

        var opaqueResult = cache.Resolve(opaque, translucentVertices: false);
        var semiResult = cache.Resolve(semi, translucentVertices: false);

        Assert.Equal(opaqueResult.MaterialIndex, semiResult.MaterialIndex);
        var material = Assert.Single(document.Materials);
        Assert.Equal((ModelAlphaMode.Mask, 1f, "cutout"),
            (material.AlphaMode, material.BaseColor.W, material.Name));
    }

    /// <summary>
    ///     The reported slot-122 black rectangle and its slot-123 control use
    ///     otherwise equivalent 16-triangle teeth art. Slot 122 binds I8,
    ///     whose intensity is also its alpha on N64 hardware; slot 123 binds
    ///     CI4 with a conventional one-bit cutout palette.
    /// </summary>
    [CorpusFact]
    public void SpiderManVenomTeeth_IntensityAlphaMatchesTheCutoutControl()
    {
        var slot122 = ParseBundle(SpiderManN64Build, SpiderManRomName, "122", out var fs122);
        using var _122 = fs122;
        var slot123 = ParseBundle(SpiderManN64Build, SpiderManRomName, "123", out var fs123);
        using var _123 = fs123;
        var modulationControl = ParseBundle(SpiderManN64Build, SpiderManRomName, "150", out var fs150);
        using var _150 = fs150;

        Assert.Equal(581, slot122.TriangleCount);
        Assert.Equal(581, slot123.TriangleCount);
        Assert.Equal(40, modulationControl.TriangleCount);

        var intensityTeeth = Assert.Single(
            slot122.Materials,
            material => material.Name.StartsWith("psxtxt_755e2673", StringComparison.Ordinal));
        var indexedTeeth = Assert.Single(
            slot123.Materials,
            material => material.Name.StartsWith("psxtxt_8aa1d98c", StringComparison.Ordinal));

        Assert.Equal(ModelAlphaMode.Mask, intensityTeeth.AlphaMode);
        Assert.Equal(ModelAlphaMode.Mask, indexedTeeth.AlphaMode);

        // Texture 357 is a 32x32 I4 image whose 1,024 texels are all intensity
        // 170. Its 40 non-semitransparent triangles use I to modulate colour,
        // not as a blanket 2/3-alpha surface.
        var constantIntensity = Assert.Single(
            modulationControl.Materials,
            material => material.Name.StartsWith("psxtxt_00000002", StringComparison.Ordinal));
        Assert.Equal(ModelAlphaMode.Opaque, constantIntensity.AlphaMode);
    }

    [CorpusFact]
    public void AverageBlendWithOneBitArt_BecomesAlphaTestSoItWritesDepth()
    {
        var document = ParseBundle("030", out var fs);
        using var _ = fs;

        // Texture 0xD51A321B: 1,687 fully transparent texels, 2,409 fully
        // opaque, none partial. Its faces carry the ABR rate-0 semi bit.
        var material = Assert.Single(
            document.Materials, m => m.Name.StartsWith("psxtxt_d51a321b", StringComparison.Ordinal));

        Assert.Equal(ModelAlphaMode.Mask, material.AlphaMode);
        Assert.Equal(1f, material.BaseColor.W);
        // The ABR suffix advertises a blend equation the material no longer
        // performs, and the viewer keys behaviour off a terminal __stN.
        Assert.DoesNotContain("__st", material.Name, StringComparison.Ordinal);
    }

    [CorpusFact]
    public void AdditiveBlend_KeepsBlendingEvenWithOneBitArt()
    {
        var document = ParseBundle("030", out var fs);
        using var _ = fs;

        // Texture 0xDD1BBB66 is one-bit art too (764 transparent, 3,332
        // opaque), so only the ABR rate can separate it from the case above.
        var material = Assert.Single(
            document.Materials, m => m.Name.StartsWith("psxtxt_dd1bbb66", StringComparison.Ordinal));

        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
        Assert.EndsWith("__st1", material.Name, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The other half of the rate-0 rule. Downtown's windows are rate-0
    ///     faces whose N64 art holds no alpha at all — the PS1 build bakes
    ///     texture 0x015E00C1 with 3,249 partial-alpha texels while the N64 copy
    ///     is 4,096 opaque ones, because the port dropped the CLUT's STP
    ///     markers. The ABR bit is then the ONLY surviving signal that the
    ///     surface is glass, so it blends at 50%. A Rosetta over every THPS1
    ///     level pair puts 2,028 triangles in this cell and finds the PS1 bake
    ///     translucent for all of them, none solid.
    /// </summary>
    [CorpusFact]
    public void AverageBlendWithNoArtAlpha_BecomesFiftyPercentGlass()
    {
        var document = ParseBundle("004", out var fs);
        using var _ = fs;

        var material = Assert.Single(
            document.Materials, m => m.Name.StartsWith("psxtxt_015e00c1", StringComparison.Ordinal));

        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
        Assert.Equal(0.5f, material.BaseColor.W);
        Assert.EndsWith("__st0", material.Name, StringComparison.Ordinal);

        // Solid geometry with the bit CLEAR must not pick up any of this: the
        // Rosetta control is 93,858 triangles opaque on both sides.
        Assert.Contains(document.Materials, m => m.AlphaMode == ModelAlphaMode.Opaque);
        Assert.All(
            document.Materials.Where(m => m.AlphaMode != ModelAlphaMode.Blend),
            m => Assert.Equal(1f, m.BaseColor.W));
    }

    /// <summary>
    ///     The reported defect itself: every face of the THPS1 medals is
    ///     rate-0 over one-bit art, so nothing in the model may blend. Each
    ///     medal is two sheets 3.55 units apart, and a non-depth-writing front
    ///     sheet let the far sheet paint through it.
    /// </summary>
    [CorpusFact]
    public void Medals_HaveNoBlendedMaterialsAtAll()
    {
        var document = ParseBundle("061", out var fs);
        using var _ = fs;

        Assert.Equal(264, document.TriangleCount);
        Assert.NotEmpty(document.Materials);
        Assert.DoesNotContain(document.Materials, m => m.AlphaMode == ModelAlphaMode.Blend);
        // Six disc sheets alpha-test their circular cutout; three rims are solid.
        Assert.Equal(6, document.Materials.Count(m => m.AlphaMode == ModelAlphaMode.Mask));
        Assert.Equal(3, document.Materials.Count(m => m.AlphaMode == ModelAlphaMode.Opaque));
    }
}
