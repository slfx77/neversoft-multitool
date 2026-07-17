using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     PSX face/vertex decode helpers shared by the character-skin,
///     level-strip, and vertex-factory paths of
///     <see cref="ModelDocumentGeometryAdapter" />.
/// </summary>
internal static class PsxGeometryHelpers
{
    internal static (Vector4 C0, Vector4 C1, Vector4 C2, Vector4 C3) ComputePsxFaceColors(
        ushort version,
        PsxFace face,
        Vector4[]? gouraudPalette)
    {
        // RGBs chunks remain authoritative in the PC/DC v6 layout. The four
        // face bytes are palette indices there too; ignoring a present palette
        // strips colored baked lighting to grayscale/white. Some v6 assets omit
        // RGBs, so retain direct intensity as the evidence-backed fallback.
        if (face.IsGouraud && gouraudPalette != null)
        {
            // RGBs entries are raw GPU RGB bytes stored here in the ordinary
            // 0..255 domain. M3dAsm_ProcessPolys writes them unchanged to
            // G3/G4 (untextured) and GT3/GT4 (textured) packets. Untextured
            // PS1 primitives therefore use display RGB (/255), while textured
            // primitives use the PS1 GPU's 128-neutral texel modulation. The
            // PC v6 renderer copies RGBs entries directly to D3D diffuse colors
            // and uses D3DTOP_MODULATE, so both of its paths use display RGB.
            var usesDisplayRgb = version == 0x06 || !face.IsTextured;
            var c0 = ResolvePaletteColor(gouraudPalette, face.R, usesDisplayRgb);
            var c1 = ResolvePaletteColor(gouraudPalette, face.G, usesDisplayRgb);
            var c2 = ResolvePaletteColor(gouraudPalette, face.B, usesDisplayRgb);
            var c3 = face.IsQuad
                ? ResolvePaletteColor(gouraudPalette, face.Mode, usesDisplayRgb)
                : c0;
            return (c0, c1, c2, c3);
        }

        if (version == 0x06 && face.IsGouraud)
        {
            var c0 = ToDirectIntensity(face.R);
            var c1 = ToDirectIntensity(face.G);
            var c2 = ToDirectIntensity(face.B);
            var c3 = face.IsQuad ? ToDirectIntensity(face.Mode) : c0;
            return (c0, c1, c2, c3);
        }

        var flat = face.IsGouraud
            ? Vector4.One
            : ToFlatColor(version, face);
        return (flat, flat, flat, flat);
    }

    private static Vector4 ResolvePaletteColor(
        Vector4[] palette,
        byte index,
        bool usesDisplayRgb)
    {
        if (index >= palette.Length)
            return Vector4.One;

        var color = palette[index];
        if (usesDisplayRgb)
            return color;

        const float ps1ModulationScale = 255f / 128f;
        return new Vector4(
            Math.Min(color.X * ps1ModulationScale, 1f),
            Math.Min(color.Y * ps1ModulationScale, 1f),
            Math.Min(color.Z * ps1ModulationScale, 1f),
            color.W);
    }

    private static Vector4 ToFlatColor(ushort version, PsxFace face)
    {
        // PS1 textured primitives modulate texels with 128 as neutral.  Flat
        // untextured primitives, and the later PC/DC direct-colour layout,
        // instead carry display RGB in the ordinary 0..255 domain.
        var divisor = version == 0x06 || !face.IsTextured ? 255f : 128f;
        return new Vector4(
            Math.Min(face.R / divisor, 1f),
            Math.Min(face.G / divisor, 1f),
            Math.Min(face.B / divisor, 1f),
            1f);
    }

    private static Vector4 ToDirectIntensity(byte intensity)
    {
        var value = intensity / 255f;
        return new Vector4(value, value, value, 1f);
    }

    internal static Vector3 ComputePsxVertexNormal(PsxMesh mesh, PsxFace face, uint vertexIndex)
    {
        var normalIndex = mesh.HasPerVertexNormals && vertexIndex < mesh.VertexCount
            ? vertexIndex
            : face.NormalIndex;
        if (normalIndex >= mesh.Normals.Count)
            return Vector3.UnitY;

        var normal = mesh.Normals[(int)normalIndex];
        return ModelDocumentGeometryAdapter.NormalizeOrDefault(new Vector3(normal.X, -normal.Y, -normal.Z));
    }

    internal static Vector2 ComputePsxTextureUv(
        ushort version,
        PsxFace face,
        int u,
        int v,
        int texWidth,
        int texHeight)
    {
        if (!face.IsTextured)
            return Vector2.Zero;

        // v6 (Spider-Man DC/PC port containers) stores UVs as u16/i16 pairs
        // in a fixed 512-texel normalized space instead of the PS1-era byte
        // texel coordinates that address the texture directly. The /512 is an
        // EMPIRICAL constant — established against the BLACKCAT.PSX locked
        // fixture and the DC/PC level sweeps (validator-clean, textures land
        // correctly) — not a decomp-verified contract; the DC exe is SH-4,
        // outside the MIPS decomp toolkit.
        if (version == 0x06)
            return new Vector2(u / 512f, v / 512f);

        // PS1 UV bytes identify integer texels and the GPU samples them with
        // nearest-neighbour filtering. glTF UVs identify texel boundaries and
        // viewers normally filter linearly; mapping byte 0 directly to 0.0
        // therefore blends the first row/column with the opposite edge under
        // REPEAT (visible as the texture's bottom row at a model's top seam).
        // Address the texel centres instead. Coordinates beyond a cropped
        // texture still repeat naturally, preserving authored tiling.
        return new Vector2(
            (u + 0.5f) / Math.Max(texWidth, 1),
            (v + 0.5f) / Math.Max(texHeight, 1));
    }

    internal static uint GetPsxFaceVertexIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    internal static bool UsesCombinedPsxCharacterAssembly(PsxMeshFile psxFile)
    {
        // A HIER chunk alone is not a character marker: level files carry one
        // for their placed animated objects (THPS1-proto skdown/skvans) and
        // must stay on the per-object level path — routing them through the
        // combined skinned assembly scatters hundreds of meshes across bind
        // pivots ("dust" renders). IsSuperModel mirrors the animation-chunk
        // flag that selects the runtime super path.
        return psxFile.HasStitchedReferences ||
               (psxFile.HasHierarchy && psxFile.IsSuperModel);
    }

    internal static HashSet<int> BuildPsxLodVariantSet(PsxMeshFile psxFile)
    {
        return psxFile.Meshes
            .Select(static mesh => (int)mesh.LodNextMeshIndex)
            .Where(index => index != ushort.MaxValue && index < psxFile.Meshes.Count)
            .ToHashSet();
    }

    internal static string ResolvePsxMeshName(PsxMeshFile psxFile, int meshIndex)
    {
        var nameHash = meshIndex < psxFile.MeshNameHashes.Length ? psxFile.MeshNameHashes[meshIndex] : 0u;
        return ModelDocumentGeometryAdapter.ResolveQbName(nameHash, $"mesh_{meshIndex:X8}");
    }

    internal static (uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate)
        GetPsxMaterialKey(PsxFace face)
    {
        // A zero/missing texture hash still carries real primitive render
        // state. THPS1 skware has untextured ABR1 planes; collapsing them into
        // the opaque fallback material makes those planes solid in glTF.
        var hash = face.IsTextured ? face.TextureHash : 0u;
        return (hash, face.IsSemiTransparent, face.IsDoubleSided, face.BlendRate);
    }

    internal static Vector4 ApplyPsxUntexturedBlend(PsxFace face, Vector4 color)
    {
        if ((face.IsTextured && face.TextureHash != 0) || !face.IsSemiTransparent)
            return color;

        var luminance = Math.Max(color.X, Math.Max(color.Y, color.Z));
        return face.BlendRate switch
        {
            // B + F: approximate additive output with white whose alpha is
            // the authored vertex intensity, matching ConvertBlendTexture.
            1 => new Vector4(1f, 1f, 1f, color.W * luminance),
            // B - F: the same intensity drives a black subtractive overlay.
            2 => new Vector4(0f, 0f, 0f, color.W * luminance),
            // B + 0.25F: quarter-strength additive approximation.
            3 => new Vector4(1f, 1f, 1f, color.W * luminance * 0.25f),
            // 0.5B + 0.5F: preserve the authored hue at half opacity.
            _ => new Vector4(color.X, color.Y, color.Z, color.W * 0.5f)
        };
    }

    /// <summary>
    ///     Converts the fixed-function renderer's display-domain RGB into the
    ///     linear multiplier required by glTF <c>COLOR_0</c>. PS1 GPU packet
    ///     colours and D3D diffuse bytes are multiplied in their native 8-bit
    ///     domain, while glTF base-colour textures are decoded from sRGB before
    ///     a linear vertex colour is applied. Writing the normalized packet
    ///     bytes directly therefore gamma-encodes them a second time in a
    ///     conforming viewer (128 displays near 188). Alpha is coverage, not
    ///     colour data, and must remain unchanged.
    /// </summary>
    internal static Vector4 DisplayRgbToLinear(Vector4 color)
    {
        return new Vector4(
            SrgbChannelToLinear(color.X),
            SrgbChannelToLinear(color.Y),
            SrgbChannelToLinear(color.Z),
            color.W);
    }

    private static float SrgbChannelToLinear(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    internal static int GetOrCreatePsxMaterial(
        ModelDocument document,
        uint textureHash,
        bool semiTransparent,
        bool doubleSided,
        int blendRate,
        MeshChecksumTextureResolver? textureProvider,
        Dictionary<uint, (int Width, int Height)> textureDims,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int> materialCache)
    {
        var key = (textureHash, semiTransparent, doubleSided, blendRate);
        if (materialCache.TryGetValue(key, out var existing))
            return existing;

        var name = textureHash == 0
            ? "untextured"
            : ModelDocumentGeometryAdapter.ResolveQbName(textureHash, $"tex_{textureHash:X8}");
        if (semiTransparent)
            name += $"__st{blendRate}";
        // Face ABR state can produce several different PNGs from the same
        // native texture. Keep the sidedness suffix on the material only so
        // otherwise-identical one/two-sided materials can share an image.
        var textureName = name;
        if (doubleSided)
            name += "__2sided";

        // PS1 backface-culls every face unless flag bit 9 is set
        // (M3dAsm_ProcessPolys @0x80099B04), so PSX materials are
        // single-sided by default — unlike the RenderMaterial default.
        var material = new RenderMaterial
        {
            Name = name,
            AlphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Opaque,
            DoubleSided = doubleSided
        };

        if (textureProvider != null && textureHash != 0)
        {
            var pngBytes = textureProvider(textureHash);
            if (pngBytes != null)
            {
                var (processed, hasAlpha) = MeshTextureHelper.ApplyColorKey(pngBytes);
                if (semiTransparent)
                {
                    processed = ConvertPsxSemiTransparentTexture(processed, blendRate);
                    hasAlpha = true;
                }

                material.TextureIndex = ModelDocumentGeometryAdapter.AddTexture(
                    document,
                    textureName,
                    processed,
                    textureHash,
                    distinguishChecksumVariantsByContent: true);
                if (hasAlpha)
                    material.AlphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Mask;
                if (ModelDocumentGeometryAdapter.TryExtractPngDimensions(processed) is { } dims)
                    textureDims[textureHash] = dims;
            }
        }

        var index = ModelDocumentGeometryAdapter.AddMaterial(document, material);
        materialCache[key] = index;
        return index;
    }

    /// <summary>
    ///     Bakes one of the four PS1 ABR blend equations into the texture,
    ///     since glTF has only OPAQUE/MASK/BLEND (face_flag_semantics.md §3b;
    ///     conversions mirror the RW BSP precedent in RwBspGltfWriter):
    ///     rate 0 (0.5B+0.5F, the common glass/water/shadow average) keeps the
    ///     texture's hue at uniform 50% alpha; rate 1 (B+F additive) bakes
    ///     luminance-to-alpha (dark→transparent, bright→white glow); rate 2
    ///     (B−F subtractive) darkens via black RGB with brightness-driven
    ///     alpha; rate 3 (B+0.25F) is quarter-strength additive.
    /// </summary>
    private static byte[] ConvertPsxSemiTransparentTexture(byte[] pngBytes, int blendRate)
    {
        return blendRate switch
        {
            1 => MeshTextureHelper.ConvertLuminanceToAlpha(pngBytes),
            2 => MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0),
            3 => MeshTextureHelper.ScaleTextureAlpha(
                MeshTextureHelper.ConvertLuminanceToAlpha(pngBytes), 0.25f),
            _ => MeshTextureHelper.ScaleTextureAlpha(pngBytes, 0.5f)
        };
    }
}
