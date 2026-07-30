using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Texture.Ps1;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
        PsxMesh mesh,
        PsxFace face,
        Vector4[]? gouraudPalette,
        bool neutralizeLitFaces = true)
    {
        // Engine-lit faces: the PS1 lit path MULTIPLIES the authored colours
        // by the normal-derived light (GTE DPCL, ProcessPolys helpers
        // 0x8009A6D4/0x8009A6FC — authored vertex colour × lit(N)). The
        // character path still neutralizes (per-level light rigs vary and the
        // viewer's own lighting approximates them well — the venom/spidey
        // verified look); the non-character prop path passes false and bakes
        // the FE light against the authored albedo instead (control.psx's
        // darker thumbsticks/bumpers are dark albedo × top-lit grey).
        if (IsEngineLitFace(version, mesh, face) && neutralizeLitFaces)
            return (Vector4.One, Vector4.One, Vector4.One, Vector4.One);

        return ComputePsxFaceColors(version, face, gouraudPalette);
    }



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
            // 0..255 domain. M3dAsm_ProcessPolys uses them as the primitive
            // colors before later GPU/GTE effects such as depth cueing. Untextured
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

    /// <summary>
    ///     True when the ENGINE computes this face's colours at runtime from
    ///     its normals instead of drawing the serialized RGBs. Spider-Man PC
    ///     (v6) derives a whole-mesh lighting bit by aggregating native face
    ///     flags (loader 0x0045377A-0x004537F2) and tests it in the model
    ///     renderer at 0x00473FDF-0x00473FE9. The PS1 engine lights the same
    ///     way but PER FACE: the loader ORs face word0 into SModel.Flags
    ///     (psx_model_load_format.md §5.3), M3d_Render loads the light/colour
    ///     matrices when model bit 2 (or item flag 0x80) is set, and
    ///     ProcessPolys draws faces whose flag bit 2 selects the lit primitive
    ///     variant via M3dAsm_GetDynamicLighting (thps2-psx-proto M3D.cpp
    ///     ~1041). Characters are typically mixed (venom 394/414 lit, docock
    ///     371/539), so the unlit remainder keeps its authored colours —
    ///     census: tools/PsxAnalyzer lit-census (levels/pickups carry ZERO lit
    ///     faces). Lit faces also export WITHOUT the PS1 packet colour so the
    ///     viewer's PS1-fidelity path falls back to normals-lit shading — the
    ///     in-game pad's darker thumbsticks/bumpers are runtime lighting, and
    ///     the unlit-packet path rendered them uniformly flat.
    /// </summary>
    internal static bool IsEngineLitFace(ushort version, PsxMesh mesh, PsxFace face)
    {
        if (version == 0x06)
            return mesh.UsesDynamicLighting;

        return mesh.UsesDynamicLighting && (face.Flags & 0x0004) != 0;
    }

    /// <summary>
    ///     The FE preview light (THPS2 final table entry @0x800B97BC = proto
    ///     Skater_CreateLight 0x800C4DA0) applied by the engine's lit path to
    ///     FE props: monochrome ambient 2176/4096 plus a light illuminating
    ///     up-facing normals at 2944/4096 and down-facing at 640/4096
    ///     (glancing/back contribution 320/4096 omitted — backface-culled).
    ///     Input is the emitted glTF-space normal (+Y up).
    /// </summary>
    internal static float ComputeFeLightIntensity(Vector3 gltfNormal)
    {
        const float ambient = 2176f / 4096f;
        const float fromAbove = 2944f / 4096f;
        const float fromBelow = 640f / 4096f;
        return ambient
               + fromAbove * MathF.Max(0f, gltfNormal.Y)
               + fromBelow * MathF.Max(0f, -gltfNormal.Y);
    }

    /// <summary>
    ///     authored albedo × FE light for a lit face corner — the engine's
    ///     DPCL multiply with the FE preview light values. Textured faces sit
    ///     in the 128-neutral modulation domain and clamp at the overbright
    ///     packet ceiling (2.0); untextured faces are display-domain 0..1
    ///     (ToFlatColor /255) and clamp at 1.0 — the console saturates their
    ///     per-vertex result at 255 (fixed 2026-07-29: the uniform 2.0 clamp
    ///     shipped >1.0 flat colours that rendered brighter than hardware).
    /// </summary>
    internal static Vector4 BakeFeLight(PsxMesh mesh, PsxFace face, int slot, Vector4 color)
    {
        var vertexIndex = GetPsxFaceVertexIndex(face, slot);
        var normal = ComputePsxVertexNormal(mesh, face, vertexIndex);
        var intensity = ComputeFeLightIntensity(normal);
        var ceiling = face.IsTextured ? 2f : 1f;
        return new Vector4(
            MathF.Min(color.X * intensity, ceiling),
            MathF.Min(color.Y * intensity, ceiling),
            MathF.Min(color.Z * intensity, ceiling),
            color.W);
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
            color.X * ps1ModulationScale,
            color.Y * ps1ModulationScale,
            color.Z * ps1ModulationScale,
            color.W);
    }

    private static Vector4 ToFlatColor(ushort version, PsxFace face)
    {
        // PS1 textured primitives modulate texels with 128 as neutral.  Flat
        // untextured primitives, and the later PC/DC direct-colour layout,
        // instead carry display RGB in the ordinary 0..255 domain.
        var divisor = version == 0x06 || !face.IsTextured ? 255f : 128f;
        return new Vector4(
            face.R / divisor,
            face.G / divisor,
            face.B / divisor,
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
    internal static Vector4 DisplayRgbToLinear(
        Vector4 color,
        bool isPs1TexturedModulation = false)
    {
        if (isPs1TexturedModulation)
        {
            return new Vector4(
                Ps1TexturedModulationToLinear(color.X),
                Ps1TexturedModulationToLinear(color.Y),
                Ps1TexturedModulationToLinear(color.Z),
                color.W);
        }

        return new Vector4(
            SrgbChannelToLinear(color.X),
            SrgbChannelToLinear(color.Y),
            SrgbChannelToLinear(color.Z),
            color.W);
    }

    /// <summary>
    ///     Recovers the raw PS1 packet bytes from the native-domain colour
    ///     returned by <see cref="ComputePsxFaceColors(ushort, PsxFace, Vector4[])" />.
    ///     Textured primitives use 128 as the neutral multiplier, whereas
    ///     untextured primitives already use ordinary 0..255 display RGB.
    /// </summary>
    internal static Vector4 ToPsxPacketColor(
        Vector4 color,
        bool isTexturedModulation)
    {
        if (!isTexturedModulation)
            return color;

        const float modulationToByteScale = 128f / 255f;
        return new Vector4(
            color.X * modulationToByteScale,
            color.Y * modulationToByteScale,
            color.Z * modulationToByteScale,
            color.W);
    }

    private static float SrgbChannelToLinear(float value)
    {
        // Preserve extended multipliers in the intermediate model. PS1
        // textured packet RGB spans 0..255 with 128 neutral, so its linear
        // modulation can exceed 1.0. Exporters must carry that separately from
        // glTF COLOR_0, whose values remain normalized.
        value = Math.Max(value, 0f);
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private static float Ps1TexturedModulationToLinear(float modulation)
    {
        // Compute a linear multiplier relative to the decoded PS1 texture's
        // neutral grey. Native GT primitives treat packet RGB 128 as neutral.
        // The corresponding 5-bit texel midpoint (16) is expanded by the PS1
        // decoder to floor(16*255/31) == 131. Dividing by that texel's linear
        // value makes a neutral-grey textured edge reproduce the same display
        // RGB as an adjacent untextured primitive carrying the same RGBs index.
        // Values above 128 intentionally return multipliers above one.
        const float packetByteScale = 128f / 255f;
        const float decodedNeutral = 131f / 255f;
        var packetDisplay = Math.Max(modulation * packetByteScale, 0f);
        return SrgbChannelToLinear(packetDisplay) / SrgbChannelToLinear(decodedNeutral);
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

        var alphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Opaque;
        int? textureIndex = null;

        if (textureProvider != null && textureHash != 0)
        {
            var pngBytes = textureProvider(textureHash);
            if (pngBytes != null)
            {
                // PS1 palette decoding already identifies exact authored CLUT
                // key entries, including duplicate key slots used by billboard
                // art. Do not run the generic DXT-style fringe heuristic:
                // nearby STP texels are blend state, not edge coverage.
                var processed = pngBytes;
                var hasAlpha = false;
                if (semiTransparent)
                {
                    var converted = ConvertPsxSemiTransparentTexture(processed, blendRate);
                    processed = converted.Bytes;
                    if (converted.HasRuntimeOpaqueTexels && blendRate is 1 or 3)
                    {
                        // The viewer applies an additive blend approximation only for
                        // terminal __st1/__st3 names. A runtime palette with
                        // ordinary texels cannot use whole-material additive
                        // blending, so keep the portable alpha approximation.
                        name += "__conditional";
                    }

                    hasAlpha = true;
                }
                else
                {
                    // STP is conditional GPU state, not source coverage. Opaque
                    // faces ignore it, so normalize the mesh-only palette
                    // markers back to opaque while retaining a transparent key.
                    (processed, hasAlpha) = NormalizeOpaquePs1Texture(processed);
                }

                textureIndex = ModelDocumentGeometryAdapter.AddTexture(
                    document,
                    textureName,
                    processed,
                    textureHash,
                    distinguishChecksumVariantsByContent: true);
                if (hasAlpha)
                    alphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Mask;
                if (ModelDocumentGeometryAdapter.TryExtractPngDimensions(processed) is { } dims)
                    textureDims[textureHash] = dims;
            }
        }

        // PS1 backface-culls every face unless flag bit 9 is set
        // (M3dAsm_ProcessPolys @0x80099B04), so PSX materials are
        // single-sided by default — unlike the RenderMaterial default.
        var material = new RenderMaterial
        {
            Name = name,
            TextureIndex = textureIndex,
            AlphaMode = alphaMode,
            DoubleSided = doubleSided
        };
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
    private static (byte[] Bytes, bool HasRuntimeOpaqueTexels) ConvertPsxSemiTransparentTexture(
        byte[] pngBytes,
        int blendRate)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var markers = FindRuntimePaletteMarkers(image);

        if (markers.HasOpaque || markers.HasStp)
        {
            // On a textured PS1 primitive, ABE/ABR only applies to texels whose
            // CLUT entry carries STP. Preserve ordinary palette entries at
            // alpha=1 even though they share the same semi-transparent face.
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        ref var pixel = ref row[x];
                        if (pixel.A == Ps1TextureDecoder.RuntimeOpaqueAlpha)
                        {
                            pixel.A = byte.MaxValue;
                            continue;
                        }

                        if (pixel.A != Ps1TextureDecoder.RuntimeSemiTransparencyAlpha)
                            continue;

                        var luminance = (pixel.R * 77 + pixel.G * 150 + pixel.B * 29) >> 8;
                        var maxChannel = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                        pixel = blendRate switch
                        {
                            // A wholly-STP texture can use the viewer's
                            // additive material without affecting ordinary
                            // texels. Keep its authored hue and encode the ABR
                            // strength in alpha. Mixed palettes retain the
                            // portable alpha-over approximation instead.
                            1 when !markers.HasOpaque =>
                                new Rgba32(pixel.R, pixel.G, pixel.B, 255),
                            1 => new Rgba32(255, 255, 255, (byte)luminance),
                            2 => new Rgba32(0, 0, 0, maxChannel),
                            3 when !markers.HasOpaque =>
                                new Rgba32(pixel.R, pixel.G, pixel.B, 64),
                            3 => new Rgba32(
                                255,
                                255,
                                255,
                                (byte)Math.Clamp((int)MathF.Round(luminance * 0.25f), 0, 255)),
                            _ => new Rgba32(pixel.R, pixel.G, pixel.B, 128)
                        };
                    }
                }
            });

            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return (stream.ToArray(), markers.HasOpaque);
        }

        // No runtime CLUT markers = a 16-bit (DC/PC v6 PowerVR) texture whose
        // alpha is authored per-texel. CUTOUT art — anything with genuinely
        // transparent holes, whether hard 1555 (alpha 0/255) or antialiased
        // 4444 wire edges (alpha 0 + graduated) — keeps its authored alpha on
        // an ABR-0 face: DC hardware source-alpha-blends the authored values,
        // so the PS1-style uniform 50% wash rendered chain-link fences far
        // too transparent (user report, THPS2 DC; SKPH's fences are 4444 with
        // graduated wire edges). Full-coverage art (glass sheets, no holes)
        // keeps the 50% average bake.
        if (blendRate == 0 && HasAuthoredCutoutAlpha(image))
            return (pngBytes, false);

        var converted = blendRate switch
        {
            1 => MeshTextureHelper.ConvertLuminanceToAlpha(pngBytes),
            2 => MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0),
            3 => MeshTextureHelper.ScaleTextureAlpha(
                MeshTextureHelper.ConvertLuminanceToAlpha(pngBytes), 0.25f),
            _ => MeshTextureHelper.ScaleTextureAlpha(pngBytes, 0.5f)
        };
        return (converted, false);
    }

    private static bool HasAuthoredCutoutAlpha(Image<Rgba32> image)
    {
        var hasHole = false;
        var hasVisible = false;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !(hasHole && hasVisible); y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A == 0)
                        hasHole = true;
                    else
                        hasVisible = true;
                }
            }
        });
        return hasHole && hasVisible;
    }

    private static (bool HasOpaque, bool HasStp) FindRuntimePaletteMarkers(Image<Rgba32> image)
    {
        var hasOpaque = false;
        var hasStp = false;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !(hasOpaque && hasStp); y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    hasOpaque |= row[x].A == Ps1TextureDecoder.RuntimeOpaqueAlpha;
                    hasStp |= row[x].A == Ps1TextureDecoder.RuntimeSemiTransparencyAlpha;
                    if (hasOpaque && hasStp)
                        break;
                }
            }
        });
        return (hasOpaque, hasStp);
    }

    private static (byte[] Bytes, bool HasAlpha) NormalizeOpaquePs1Texture(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var foundRuntimeMarker = false;
        var hasAlpha = false;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    if (pixel.A == Ps1TextureDecoder.RuntimeOpaqueAlpha ||
                        pixel.A == Ps1TextureDecoder.RuntimeSemiTransparencyAlpha)
                    {
                        pixel.A = byte.MaxValue;
                        foundRuntimeMarker = true;
                    }
                    else if (pixel.A < byte.MaxValue)
                    {
                        hasAlpha = true;
                    }
                }
            }
        });

        if (!foundRuntimeMarker)
            return (pngBytes, hasAlpha);

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return (stream.ToArray(), hasAlpha);
    }
}
