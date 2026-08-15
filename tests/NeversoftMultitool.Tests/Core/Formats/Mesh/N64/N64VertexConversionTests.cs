using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the two per-vertex conversions the N64 writer performs, without
///     building a synthetic render bank: UV addressing and the COLOR_0 domain.
/// </summary>
public sealed class N64VertexConversionTests
{
    private static N64RenderBankFile.N64Vertex Vertex(byte r, byte g, byte b, byte a = 255)
    {
        return new N64RenderBankFile.N64Vertex(0, 0, 0, 0, 0, r, g, b, a);
    }

    // ── UV addressing ──────────────────────────────────────────────────

    /// <summary>
    ///     Stored ST are integer texel INDICES in S10.5 (spans run 0..N−1 over
    ///     an N-wide sheet), so they must address texel centres. Landing on
    ///     0.0/1.0 puts a linearly filtered sample on the boundary, where it
    ///     blends with the wrapped opposite edge — the reported seam.
    /// </summary>
    [Theory]
    [InlineData((short)0, 64, 0.5f / 64f)]
    [InlineData((short)(63 * 32), 64, 63.5f / 64f)]
    [InlineData((short)0, 256, 0.5f / 256f)]
    [InlineData((short)(255 * 32), 256, 255.5f / 256f)]
    public void ComputeN64TextureUv_AddressesTexelCentres(short s, int width, float expectedU)
    {
        var uv = N64ModelWriter.ComputeN64TextureUv(s, 0, width, width);

        Assert.Equal(expectedU, uv.X, 6);
        Assert.NotEqual(0f, uv.X);
        Assert.NotEqual(1f, uv.X);
    }

    /// <summary>
    ///     The PS1 and N64 writers convert the same authored art, so a given
    ///     texel index must land on the same coordinate in both. Pinning them
    ///     together stops the two paths drifting apart again.
    /// </summary>
    [Theory]
    [InlineData(0, 64)]
    [InlineData(31, 64)]
    [InlineData(63, 64)]
    [InlineData(127, 128)]
    public void ComputeN64TextureUv_MatchesThePsxHelperForTheSameTexel(int texel, int size)
    {
        var n64 = N64ModelWriter.ComputeN64TextureUv((short)(texel * 32), (short)(texel * 32), size, size);
        var psx = PsxGeometryHelpers.ComputePsxTextureUv(
            version: 0x04,
            new PsxFace { IsTextured = true },
            texel,
            texel,
            size,
            size);

        Assert.Equal(psx.X, n64.X, 6);
        Assert.Equal(psx.Y, n64.Y, 6);
    }

    [Fact]
    public void ComputeN64TextureUv_BeyondTheSheet_StillTiles()
    {
        var uv = N64ModelWriter.ComputeN64TextureUv(64 * 32, 0, 64, 64);

        Assert.True(uv.X > 1f, "authored tiling past the sheet must survive");
    }

    // ── COLOR_0 domain ─────────────────────────────────────────────────

    /// <summary>
    ///     An unlit vertex's bytes are display-domain values the RDP emits
    ///     verbatim, while glTF COLOR_0 is a linear multiplier. Writing the
    ///     normalized byte straight through gamma-encodes it twice, which
    ///     displayed ambient 70/255 at roughly 144.
    /// </summary>
    [Fact]
    public void ComputeN64VertexColour_UnlitVertex_IsLinearized()
    {
        var colour = N64ModelWriter.ComputeN64VertexColour(Vertex(70, 70, 70), hasNormals: false, rig: null);

        Assert.Equal(0.061246f, colour.X, 5);
        Assert.Equal(colour.X, colour.Y, 6);
        Assert.Equal(colour.X, colour.Z, 6);
    }

    [Fact]
    public void ComputeN64VertexColour_LitVertex_IsTheRigShadeLinearized()
    {
        // A zero normal lands on pure ambient, which is the hardware result for
        // that input rather than a chosen fallback.
        var rig = new N64LightRig(
            new Vector3(70f / 255f, 70f / 255f, 70f / 255f),
            new Vector3(105f / 255f, 105f / 255f, 105f / 255f),
            new Vector3(0f, -1f, 0f));

        var colour = N64ModelWriter.ComputeN64VertexColour(Vertex(0, 0, 0), hasNormals: true, rig);

        Assert.Equal(0.061246f, colour.X, 5);
    }

    /// <summary>
    ///     0 and 255 are fixed points of the transform. This is the guard that
    ///     keeps the two halves of the reported "too bright" defect separate:
    ///     linearization cannot darken a white vertex, so it can never be the
    ///     fix for geometry that exports white for want of a light rig.
    /// </summary>
    [Fact]
    public void ComputeN64VertexColour_EndpointsAreUnchanged()
    {
        var black = N64ModelWriter.ComputeN64VertexColour(Vertex(0, 0, 0), hasNormals: false, rig: null);
        var white = N64ModelWriter.ComputeN64VertexColour(Vertex(255, 255, 255), hasNormals: false, rig: null);
        var litWithoutRig = N64ModelWriter.ComputeN64VertexColour(Vertex(0, 0, 0), hasNormals: true, rig: null);

        Assert.Equal(0f, black.X, 6);
        Assert.Equal(1f, white.X, 6);
        Assert.Equal(Vector4.One, litWithoutRig);
    }

    /// <summary>
    ///     Alpha is coverage, not colour. Light shafts fade through it, so a
    ///     transform applied there would change real translucency.
    /// </summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)128)]
    [InlineData((byte)255)]
    public void ComputeN64VertexColour_AlphaIsNeverTransformed(byte alpha)
    {
        var colour = N64ModelWriter.ComputeN64VertexColour(
            Vertex(200, 100, 50, alpha), hasNormals: false, rig: null);

        Assert.Equal(alpha / 255f, colour.W, 6);
    }

    [Fact]
    public void ComputeN64Normal_ZeroNormal_FallsBackWithoutProducingNaN()
    {
        var normal = N64ModelWriter.ComputeN64Normal(Vertex(0, 0, 0), hasNormals: true);

        Assert.Equal(Vector3.UnitY, normal);
    }
}
