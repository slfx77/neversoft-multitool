using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using AlphaMode = SharpGLTF.Materials.AlphaMode;
using Image = SixLabors.ImageSharp.Image;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Writes parsed PS2 GEOM scene data to glTF 2.0 (.glb) files.
///     GEOM leaves have texture checksums but no full material data —
///     material properties (clamp, alpha) are extracted from DMA chain GS registers.
/// </summary>
public static class Ps2GeomGltfWriter
{
    /// <summary>
    ///     Delegate that resolves a raw TEX0_1 GS register value to a texture checksum.
    ///     Used for THPS4 GEOM files where CGeomNode.texture_checksum is always 0
    ///     and textures are identified by VRAM addresses embedded in the DMA chain.
    ///     The group checksum disambiguates double-buffered VRAM banks where different
    ///     texture groups reuse the same TBP/CBP addresses.
    ///     Returns 0 if the TEX0 value cannot be resolved.
    /// </summary>
    public delegate uint Tex0Resolver(ulong dmaTex0, uint groupChecksum);

    /// <summary>
    ///     Writes a parsed PS2 GEOM scene to a .glb file.
    ///     GEOM leaves have texture checksums but no full material data.
    ///     Material properties (clamp, alpha) are extracted from DMA chain GS registers.
    ///     For THPS4 files where texture_checksum is 0, the tex0Resolver maps
    ///     DMA chain TEX0 register values to texture checksums via VRAM simulation.
    /// </summary>
    public static int Write(Ps2GeomScene geomScene, string outputPath,
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null,
        Tex0Resolver? tex0Resolver = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<GeomMaterialKey, MaterialBuilder>();
        var totalTriangles = 0;

        foreach (var leaf in geomScene.Leaves)
        {
            if (leaf.Vertices.Length < 3) continue;

            // Resolve texture checksum: use node field if non-zero (THUG/THUG2),
            // otherwise fall back to DMA TEX0 VRAM lookup (THPS4)
            var texChecksum = leaf.TextureChecksum;
            if (texChecksum == 0 && leaf.DmaTex0 != 0 && tex0Resolver != null)
                texChecksum = tex0Resolver(leaf.DmaTex0, leaf.GroupChecksum);

            var name = QbKey.TryResolve(leaf.Checksum) ?? $"node_{leaf.Checksum:X8}";
            var material = GetOrCreateGeomMaterial(texChecksum, leaf.DmaClamp1,
                leaf.DmaAlpha1, leaf.DmaTest1, materialCache, textureProvider);

            var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(name);
            var prim = gltfMesh.UsePrimitive(material);
            var tris = Ps2SceneGltfWriter.AddTriangleStrip(prim, leaf.Vertices);

            if (tris == 0) continue;

            totalTriangles += tris;
            var node = new NodeBuilder(name);
            scene.AddRigidMesh(gltfMesh, node);
        }

        if (totalTriangles == 0)
            return 0;

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
    }

    private static MaterialBuilder GetOrCreateGeomMaterial(
        uint textureChecksum, ulong clamp1, ulong alpha1, ulong test1,
        Dictionary<GeomMaterialKey, MaterialBuilder> cache,
        Ps2SceneGltfWriter.TextureProvider? textureProvider)
    {
        // Decode GS register fields for material key
        var clampBits = (byte)(clamp1 & 0x0F); // WMS (bits 0-1) + WMT (bits 2-3)
        var alphaBlend = (byte)(alpha1 & 0xFF); // A,B,C,D fields
        var ate = (test1 & 1) != 0;
        var aref = (byte)((test1 >> 4) & 0xFF);

        var key = new GeomMaterialKey(textureChecksum, clampBits, alphaBlend, aref);
        if (cache.TryGetValue(key, out var existing))
            return existing;

        var matName = textureChecksum != 0
            ? QbKey.TryResolve(textureChecksum) ?? $"tex_{textureChecksum:X8}"
            : "default";

        // Decode blend equation fields from ALPHA_1 register.
        // Cv = ((A-B)*C)>>7 + D where A/B/D select Cs/Cd/0, C selects As/Ad/FIX.
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;

        // Additive: A=Cs(0), B=0(2), D=Cd(1) -> Cs*C + Cd (foam, glow, water spray)
        var isAdditive = aField == 0 && bField == 2 && dField == 1;
        // Subtractive: A=0(2), B=Cs(0), D=Cd(1) -> (0-Cs)*C + Cd = Cd - Cs*C (shadows)
        var isSubtractive = aField == 2 && bField == 0 && dField == 1;

        var builder = new MaterialBuilder(matName)
            .WithUnlitShader()
            .WithBaseColor(Vector4.One)
            .WithDoubleSide(true);

        if (textureProvider != null && textureChecksum != 0)
        {
            var pngBytes = textureProvider(textureChecksum);
            if (pngBytes != null)
            {
                // For additive blend, convert texture to luminance-alpha:
                // RGB -> white, Alpha = max(R,G,B). This approximates additive
                // blending (black=invisible, white=bright overlay) using glTF BLEND
                // which doesn't natively support additive.
                if (isAdditive)
                    pngBytes = ConvertBlendTexture(pngBytes, 255, 255, 255);
                else if (isSubtractive)
                    pngBytes = ConvertBlendTexture(pngBytes, 0, 0, 0);

                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);

                // Set texture wrap mode from CLAMP_1 register.
                // WMS/WMT: 0=REPEAT, 1=CLAMP. Only simple modes used in practice.
                var wms = clampBits & 0x03;
                var wmt = (clampBits >> 2) & 0x03;
                if (wms != 0 || wmt != 0)
                {
                    var wrapS = wms != 0
                        ? TextureWrapMode.CLAMP_TO_EDGE
                        : TextureWrapMode.REPEAT;
                    var wrapT = wmt != 0
                        ? TextureWrapMode.CLAMP_TO_EDGE
                        : TextureWrapMode.REPEAT;
                    builder.GetChannel(KnownChannel.BaseColor)
                        .Texture
                        .WithSampler(wrapS, wrapT);
                }
            }
        }

        // Alpha handling from GS registers.
        // ALPHA_1 low byte: 0x0A/0x1A = opaque (output=Cs). Anything else = blending.
        // TEST_1 alpha test: PackTEST(1,AGEQUAL,Aref,KEEP,0,0,1,ZGEQUAL)
        //   AREF=0: alpha >= 0 always passes -> truly OPAQUE.
        //   AREF=1: discards alpha=0 pixels -> MASK cutout (fences, foliage, etc.)
        //   AREF>=2: higher-threshold cutout -> MASK with visible cutoff.
        var isOpaqueBlend = alphaBlend is 0x0A or 0x1A or 0x00;
        if (isAdditive)
        {
            builder.WithAlpha(AlphaMode.BLEND);

            // For FIX-mode additive (Cs*FIX/128 + Cd), scale brightness
            if (cField == 2)
            {
                var fix = (byte)((alpha1 >> 32) & 0xFF);
                var intensity = Math.Min(fix / 128f, 1f);
                builder.WithBaseColor(new Vector4(intensity, intensity, intensity, 1f));
            }
        }
        else if (isSubtractive)
        {
            // Subtractive: Cd - Cs*FIX/128. Texture converted to black + luminance alpha.
            // BLEND output = black * alpha + dst * (1-alpha) = dst * (1-alpha) -> darkens.
            builder.WithAlpha(AlphaMode.BLEND);

            if (cField == 2)
            {
                var fix = (byte)((alpha1 >> 32) & 0xFF);
                var opacity = Math.Min(fix / 128f, 1f);
                builder.WithBaseColor(new Vector4(0f, 0f, 0f, opacity));
            }
            else
            {
                builder.WithBaseColor(new Vector4(0f, 0f, 0f, 1f));
            }
        }
        else if (!isOpaqueBlend)
        {
            // Fixed-blend opacity: C field (bits 4-5) == 2 means FIX mode.
            // FIX value in ALPHA_1 bits 32-39. Opacity = FIX/128.
            // High FIX values (>= threshold) are treated as OPAQUE to avoid
            // z-sorting artifacts in glTF viewers that don't depth-sort BLEND.
            if (cField == 2)
            {
                var fix = (byte)((alpha1 >> 32) & 0xFF);
                if (fix < Ps2SceneGltfWriter.FixBlendOpaqueThreshold)
                {
                    builder.WithAlpha(AlphaMode.BLEND);
                    builder.WithBaseColor(new Vector4(1f, 1f, 1f, fix / 128f));
                }
                // else: fix >= threshold -> leave as default OPAQUE
            }
            else
            {
                // Non-FIX blend modes (source-alpha, dest-alpha) -> always BLEND
                builder.WithAlpha(AlphaMode.BLEND);
            }
        }
        else if (ate && aref >= 1)
        {
            builder.WithAlpha(AlphaMode.MASK, aref / 255f);
        }

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    ///     Converts a texture for additive/subtractive blend approximation in glTF.
    ///     Sets alpha = max(R,G,B) (luminance) and RGB = specified color.
    ///     Additive (white): bright areas render as opaque white overlay, dark = transparent.
    ///     Subtractive (black): bright areas render as dark shadow overlay, dark = transparent.
    /// </summary>
    private static byte[] ConvertBlendTexture(byte[] pngBytes, byte r, byte g, byte b)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    var luminance = Math.Max(p.R, Math.Max(p.G, p.B));
                    row[x] = new Rgba32(r, g, b, (byte)(luminance * p.A / 255));
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Composite key for GEOM material caching. Different leaves with the same texture
    ///     may need different materials if they have different clamp or alpha settings.
    /// </summary>
    private readonly record struct GeomMaterialKey(
        uint TextureChecksum,
        byte ClampBits,
        byte AlphaBlend,
        byte AlphaRef);
}
