using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

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

    private ModelDocument ParseBundle(string bundlePath, out IArchiveFileSystem fs)
    {
        var romPath = paths.FindSampleFile(Thps1N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS1 N64 ROM sample not available");
        fs = ArchiveFileSystem.TryOpen(romPath!)!;
        var backend = ArchiveAssetBackend.TryOpen(romPath!)!;
        var entry = backend.FindByPath(bundlePath)!;
        var source = new ArchiveAssetSource(backend, entry);

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = "n64_alpha",
            SourceKind = ModelSourceKind.N64Model
        });
    }

    [Fact]
    public void AverageBlendWithOneBitArt_BecomesAlphaTestSoItWritesDepth()
    {
        var document = ParseBundle("models/030/geometry.psx.n64", out var fs);
        using var _ = fs;

        // Texture 0xD51A321B: 1,687 fully transparent texels, 2,409 fully
        // opaque, none partial. Its faces carry the ABR rate-0 semi bit.
        var material = Assert.Single(
            document.Materials.Where(m => m.Name.StartsWith("psxtxt_d51a321b", StringComparison.Ordinal)));

        Assert.Equal(ModelAlphaMode.Mask, material.AlphaMode);
        // The ABR suffix advertises a blend equation the material no longer
        // performs, and the viewer keys behaviour off a terminal __stN.
        Assert.DoesNotContain("__st", material.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void AdditiveBlend_KeepsBlendingEvenWithOneBitArt()
    {
        var document = ParseBundle("models/030/geometry.psx.n64", out var fs);
        using var _ = fs;

        // Texture 0xDD1BBB66 is one-bit art too (764 transparent, 3,332
        // opaque), so only the ABR rate can separate it from the case above.
        var material = Assert.Single(
            document.Materials.Where(m => m.Name.StartsWith("psxtxt_dd1bbb66", StringComparison.Ordinal)));

        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
        Assert.EndsWith("__st1", material.Name, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The reported defect itself: every face of the THPS1 medals is
    ///     rate-0 over one-bit art, so nothing in the model may blend. Each
    ///     medal is two sheets 3.55 units apart, and a non-depth-writing front
    ///     sheet let the far sheet paint through it.
    /// </summary>
    [Fact]
    public void Medals_HaveNoBlendedMaterialsAtAll()
    {
        var document = ParseBundle("models/061/geometry.psx.n64", out var fs);
        using var _ = fs;

        Assert.Equal(264, document.TriangleCount);
        Assert.NotEmpty(document.Materials);
        Assert.DoesNotContain(document.Materials, m => m.AlphaMode == ModelAlphaMode.Blend);
        // Six disc sheets alpha-test their circular cutout; three rims are solid.
        Assert.Equal(6, document.Materials.Count(m => m.AlphaMode == ModelAlphaMode.Mask));
        Assert.Equal(3, document.Materials.Count(m => m.AlphaMode == ModelAlphaMode.Opaque));
    }
}
