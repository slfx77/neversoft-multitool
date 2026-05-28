using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public static partial class Ps2GeomGltfWriter
{
    /// <summary>
    ///     Converts an additive texture into a glTF alpha-blend approximation while
    ///     preserving the source hue. For PS2 additive output (dst + src), store the
    ///     source contribution as premultiplied colour represented by RGB + alpha:
    ///     alpha = max(src.rgb), RGB = src.rgb / alpha. This avoids turning yellow
    ///     city-light/sky overlays into solid white sheets.
    /// </summary>
    private static byte[] ConvertAdditiveBlendTexture(byte[] pngBytes)
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
                    var maxChannel = Math.Max(p.R, Math.Max(p.G, p.B));
                    if (maxChannel == 0 || p.A == 0)
                    {
                        row[x] = new Rgba32(0, 0, 0, 0);
                        continue;
                    }

                    var alpha = (byte)(maxChannel * p.A / 255);
                    row[x] = new Rgba32(
                        (byte)Math.Min(255, p.R * 255 / maxChannel),
                        (byte)Math.Min(255, p.G * 255 / maxChannel),
                        (byte)Math.Min(255, p.B * 255 / maxChannel),
                        alpha);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Converts a texture for additive/subtractive blend approximation in glTF.
    ///     Sets alpha = max(R,G,B) (luminance) and RGB = specified color.
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
    ///     Strategy switch for C=Ad (destination-alpha blend) materials. The
    ///     game's destination alpha is a runtime framebuffer property we can't
    ///     reproduce in glTF; this lets the user pick the closest stand-in.
    /// </summary>
    private enum DestAlphaOverride
    {
        Opaque,
        Blend
    }

    /// <summary>
    ///     Coarse classification of a PNG's alpha channel into three buckets used for
    ///     glTF alphaMode selection.
    ///     <see cref="AllOpaque" />: all pixels at or near full opacity.
    ///     <see cref="Bimodal" />: most pixels are at the extremes (a=0 or a=255) with at
    ///     most a thin antialiasing fringe at the boundaries — a hard-edge cutout
    ///     (fences, foliage, signs, decals over a transparent quad). Maps to MASK so
    ///     the result writes to the depth buffer and occludes correctly.
    ///     <see cref="Graduated" />: a substantial fraction of pixels fall in the
    ///     mid-alpha range — true soft falloff such as shadows, smoke, light beams.
    ///     Maps to BLEND.
    /// </summary>
    private enum AlphaProfile
    {
        AllOpaque,
        Bimodal,
        Graduated
    }

    /// <summary>
    ///     Composite key for GEOM material caching. Different leaves with the same texture
    ///     may need different materials if they have different clamp or alpha settings.
    ///     <see cref="BakedVertexTintRgba" /> packs the per-leaf average vertex colour
    ///     as 0xFF_RR_GG_BB (sentinel byte 0xFF in the high byte signals "bake active";
    ///     0x00000000 means "no bake"). When active, the texture is modulated in 8-bit
    ///     display space and per-vertex colours are emitted as (1,1,1,1). Used for
    ///     OPAQUE worldzone leaves whose baked AO/lighting otherwise reads bluish-dim
    ///     through glTF viewers' gamma-correct vertex modulation.
    /// </summary>
    private readonly record struct GeomMaterialKey(
        uint TextureChecksum,
        byte ClampBits,
        byte AlphaBlend,
        byte AlphaRef,
        byte FixValue,
        bool PreferCutout,
        bool PreferBlend,
        uint BakedVertexTintRgba = 0u);

    private readonly record struct GeomBucketKey(GeomMaterialKey Material, int UniqueBlendOrdinal);

    private readonly record struct DestinationAlphaMaskCandidate(
        LeafGeometryKey Geometry,
        uint TextureChecksum,
        Ps2GeomLeaf Leaf);

    private readonly record struct LeafGeometryKey(int VertexCount, Vector3 Min, Vector3 Max);

    private readonly record struct UvPair(float SourceU, float SourceV, float MaskU, float MaskV);

    private readonly record struct UvAffineTransform(
        float MaskUFromSourceU,
        float MaskUFromSourceV,
        float MaskUOffset,
        float MaskVFromSourceU,
        float MaskVFromSourceV,
        float MaskVOffset)
    {
        public (float U, float V) Transform(float sourceU, float sourceV)
        {
            var u = MaskUFromSourceU * sourceU + MaskUFromSourceV * sourceV + MaskUOffset;
            var v = MaskVFromSourceU * sourceU + MaskVFromSourceV * sourceV + MaskVOffset;
            return (u, v);
        }
    }

    private readonly record struct GeomMaterialInfo(MaterialBuilder Material, string AlphaMode);

    private readonly record struct GlbChunk(uint Type, byte[] Data);

    private sealed class GeomMeshBucket(
        string name,
        string alphaMode,
        MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> mesh,
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> primitive,
        HashSet<(Vector3, Vector3, Vector3)> dedup,
        bool preserveVertexAlpha,
        bool bakeVertexColorsToWhite = false)
    {
        private bool _hasBounds;
        private Vector3 _max = new(float.MinValue);

        private Vector3 _min = new(float.MaxValue);
        public string Name { get; } = name;
        public string AlphaMode { get; } = alphaMode;
        public MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> Mesh { get; } = mesh;

        public PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> Primitive
        {
            get;
        } = primitive;

        public HashSet<(Vector3, Vector3, Vector3)> Dedup { get; } = dedup;
        public bool PreserveVertexAlpha { get; } = preserveVertexAlpha;
        public bool BakeVertexColorsToWhite { get; } = bakeVertexColorsToWhite;
        public int TriangleCount { get; set; }

        public void Include(IReadOnlyList<Ps2Vertex> vertices)
        {
            foreach (var vertex in vertices)
            {
                _min = Vector3.Min(_min, vertex.Position);
                _max = Vector3.Max(_max, vertex.Position);
                _hasBounds = true;
            }
        }

        public bool TryGetCenter(out Vector3 center)
        {
            center = Vector3.Zero;
            if (!_hasBounds)
                return false;

            center = (_min + _max) * 0.5f;
            return center.LengthSquared() > 1e-8f;
        }
    }
}
