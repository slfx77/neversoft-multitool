using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Builds one glTF material per (texture slot, blend state) a bank
///     references, mirroring the PS1 material key so the same texture used
///     opaque and semi-transparent yields distinct materials. Face blend
///     semantics come from blob B's PS1 flag word (bit 6 ABE, bits 7-8 ABR),
///     while an intensity texture can independently retain the N64 runtime's
///     authored forced-blend class. Bit 9 disables backface culling.
/// </summary>
internal sealed class N64MaterialCache(
    ModelDocument document,
    Func<int, N64ModelCompanions.N64ResolvedTexture?> textureProvider)
{
    private readonly Dictionary<
        (int Slot, ModelAlphaMode Mode, bool DoubleSided, int Rate,
         bool ForcedBlendSemi, float ForcedBlendAlpha), int>
        _materials = [];
    private readonly Dictionary<int, (int Width, int Height)> _sizes = [];

    public int TexturedFaces { get; private set; }
    public int UntexturedFaces { get; private set; }

    public (int MaterialIndex, (int Width, int Height) Size) Resolve(
        N64RenderBankFile.N64Triangle triangle,
        bool translucentVertices)
    {
        var flags = triangle.FaceFlags;
        var semi = (flags & PsxFaceFlags.SemiTransparent) != 0;
        var doubleSided = (flags & PsxFaceFlags.DoubleSided) != 0;
        var rate = semi ? (flags & PsxFaceFlags.BlendRateMask) >> 7 : 0;

        var texture = triangle.TextureSlot > 0 ? textureProvider(triangle.TextureSlot) : null;
        var size = texture != null ? (texture.Width, texture.Height) : (1, 1);
        _sizes[triangle.TextureSlot] = size;

        var (mode, alpha) = ResolveBlendState(rate, semi, translucentVertices, texture);
        // Non-semi authored forced-blend faces and semi/rate-0 faces can both
        // resolve to Blend/rate 0 while requiring different alpha and names.
        // Scope that extra identity to authored forced-blend textures so this
        // fix does not split legacy CI/RGBA/IA materials outside B1.
        var forcesBlend = texture is { ForcesBlend: true };
        var forcedBlendSemi = forcesBlend && semi;
        var forcedBlendAlpha = forcesBlend ? alpha : 1f;
        var key = (triangle.TextureSlot, mode, doubleSided, rate,
            forcedBlendSemi, forcedBlendAlpha);
        if (_materials.TryGetValue(key, out var existing))
        {
            Count(triangle.TextureSlot);
            return (existing, size);
        }

        // The ABR suffix advertises a blend equation, and the viewer keys its
        // additive approximation off a terminal __st1/__st3, so only carry it
        // when the material really does blend.
        var blendSuffix = semi && mode == ModelAlphaMode.Blend ? $"__st{rate}" : string.Empty;
        var sideSuffix = doubleSided ? "__2sided" : string.Empty;
        var baseName = texture?.Name ?? "n64_untextured";
        var material = new RenderMaterial
        {
            Name = baseName + blendSuffix + sideSuffix,
            BaseColor = new Vector4(1f, 1f, 1f, alpha),
            DoubleSided = doubleSided,
            // Always unlit, like the PS1 path. The console shades these
            // surfaces diffusely with no specular term at all, whereas glTF's
            // metallic-roughness model always adds a dielectric highlight -
            // which reads as glossiness the game never had. Normals are still
            // exported for consumers that want them.
            Unlit = true,
            AlphaMode = mode,
            // 1-bit art: keep any texel the console would have drawn.
            AlphaCutoff = 0.5f
        };

        var index = ModelDocumentGeometryAdapter.AddMaterial(document, material);
        if (texture != null)
        {
            // Wrap is a property of the dictionary SLOT, which is already the
            // dedup key here, so two materials sharing a slot cannot disagree
            // about it and no content-distinguishing variant key is needed.
            var textureIndex = ModelDocumentGeometryAdapter.AddTexture(
                document, texture.Name, texture.Png, (uint)triangle.TextureSlot,
                texture.WrapU, texture.WrapV);
            document.Materials[index].TextureIndex = textureIndex;
        }

        _materials[key] = index;
        Count(triangle.TextureSlot);
        return (index, size);
    }

    /// <summary>PS1 ABR rate 0 is 0.5·background + 0.5·face.</summary>
    private const float AverageBlendAlpha = 0.5f;

    /// <summary>
    ///     Works out how a face composites. The PS1 semi-transparent bit alone
    ///     does not decide it, and neither does the art alone. An authored N64
    ///     forced-blend texture state is evaluated separately from the art's
    ///     alpha profile so binary cloud textures cannot collapse to Mask.
    ///     <para>
    ///         Rates 1-3 (additive, subtractive, quarter-additive) composite
    ///         with the framebuffer by EQUATION rather than by texel alpha, so
    ///         no alpha content can stand in for them and they always blend.
    ///     </para>
    ///     <para>
    ///         Rate 0 (the 50/50 average) was a PER-TEXEL state on the PS1,
    ///         armed only where the CLUT entry carried the STP marker — and the
    ///         port dropped the marker, so the N64 file can only say "this face
    ///         is an average blend". Which way to read that is settled by a
    ///         Rosetta over every THPS1 level pair, joining on the texture ids
    ///         the ports reuse verbatim:
    ///         <list type="bullet">
    ///             <item>
    ///                 Rate 0 over art with NO alpha channel at all — 2,028
    ///                 triangles — is TRANSLUCENT in the PS1 bake for every
    ///                 single one, none solid. The flag is the only surviving
    ///                 signal that the surface is glass, so it blends at 50%.
    ///                 Downtown's windows are 164 of those triangles: PS1
    ///                 texture 0x015E00C1 bakes 3,249 partial-alpha texels while
    ///                 its N64 copy is 4,096 opaque ones.
    ///             </item>
    ///             <item>
    ///                 Rate 0 over art that DOES carry a transparency key keeps
    ///                 alpha testing. Blanket-blending would make the THPS1
    ///                 medals half-transparent and cost them the depth write
    ///                 that stops their far sheet painting over the near one.
    ///                 This is the rule's known lossy edge: 1,357 level
    ///                 triangles in that cell are translucent on the PS1 side
    ///                 and alpha-test here, keeping their holes and their depth.
    ///             </item>
    ///         </list>
    ///         The control holds the reading up: 93,858 triangles with the bit
    ///         CLEAR are opaque on both sides, against 12 that are not.
    ///     </para>
    /// </summary>
    internal static (ModelAlphaMode Mode, float Alpha) ResolveBlendState(
        int blendRate,
        bool semi,
        bool translucentVertices,
        N64ModelCompanions.N64ResolvedTexture? texture)
    {
        if (blendRate != 0 || translucentVertices || texture is { HasGraduatedAlpha: true })
            return (ModelAlphaMode.Blend, 1f);

        if (texture is { ForcesBlend: true })
        {
            return (ModelAlphaMode.Blend,
                semi && blendRate == 0 ? AverageBlendAlpha : 1f);
        }

        if (semi && texture is not { HasCutout: true })
            return (ModelAlphaMode.Blend, AverageBlendAlpha);

        return texture is { HasCutout: true }
            ? (ModelAlphaMode.Mask, 1f)
            : (ModelAlphaMode.Opaque, 1f);
    }

    private void Count(int slot)
    {
        if (slot > 0)
            TexturedFaces++;
        else
            UntexturedFaces++;
    }
}
