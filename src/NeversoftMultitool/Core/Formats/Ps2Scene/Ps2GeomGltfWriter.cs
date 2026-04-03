using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh;
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

        var (model, triangles) = Build(geomScene, textureProvider, tex0Resolver);
        if (triangles == 0) return 0;
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    internal static (ModelRoot Model, int Triangles) Build(Ps2GeomScene geomScene,
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null,
        Tex0Resolver? tex0Resolver = null)
    {
        var scene = new SceneBuilder();
        var materialCache = new Dictionary<GeomMaterialKey, MaterialBuilder>();
        var buckets = new Dictionary<GeomMaterialKey, GeomMeshBucket>();
        var totalTriangles = 0;
        var isWorldZoneScene = IsWorldZoneScene(geomScene);
        var triangleEdgeLimit = GetWorldZoneTriangleEdgeLimit(geomScene, isWorldZoneScene);

        foreach (var leaf in geomScene.Leaves)
        {
            if (leaf.Vertices.Length < 3) continue;

            if (isWorldZoneScene && ShouldSkipWorldZoneLeaf(leaf))
                continue;

            var texChecksum = leaf.TextureChecksum;
            if (texChecksum == 0 && leaf.DmaTex0 != 0 && tex0Resolver != null)
                texChecksum = tex0Resolver(leaf.DmaTex0, leaf.GroupChecksum);

            var key = CreateGeomMaterialKey(texChecksum, leaf.DmaClamp1, leaf.DmaAlpha1, leaf.DmaTest1);
            var bucket = GetOrCreateBucket(key, materialCache, buckets, textureProvider);
            var leafEdgeLimit = !float.IsPositiveInfinity(triangleEdgeLimit) && leaf.Vertices.All(v => !v.HasNormal)
                ? triangleEdgeLimit
                : float.PositiveInfinity;
            var tris = Ps2SceneGltfWriter.AddTriangleStrip(bucket.Primitive, leaf.Vertices,
                dedup: bucket.Dedup,
                maxTriangleEdgeLength: leafEdgeLimit,
                resetOnRestart: isWorldZoneScene);

            if (tris == 0) continue;

            totalTriangles += tris;
            bucket.TriangleCount += tris;
        }

        foreach (var bucket in buckets.Values)
        {
            if (bucket.TriangleCount == 0)
                continue;

            var node = new NodeBuilder(bucket.Name);
            scene.AddRigidMesh(bucket.Mesh, node);
        }

        return (scene.ToGltf2(), totalTriangles);
    }

    private static GeomMeshBucket GetOrCreateBucket(
        GeomMaterialKey key,
        Dictionary<GeomMaterialKey, MaterialBuilder> materialCache,
        Dictionary<GeomMaterialKey, GeomMeshBucket> buckets,
        Ps2SceneGltfWriter.TextureProvider? textureProvider)
    {
        if (buckets.TryGetValue(key, out var existing))
            return existing;

        var material = GetOrCreateGeomMaterial(key, materialCache, textureProvider);
        var name = key.TextureChecksum != 0
            ? QbKey.TryResolve(key.TextureChecksum) ?? $"tex_{key.TextureChecksum:X8}"
            : $"geom_{buckets.Count:D4}";
        var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(name);
        var bucket = new GeomMeshBucket(name, mesh, mesh.UsePrimitive(material),
            new HashSet<(Vector3, Vector3, Vector3)>());
        buckets[key] = bucket;
        return bucket;
    }

    private static bool IsWorldZoneScene(Ps2GeomScene geomScene)
    {
        return geomScene.Leaves.Count >= 500 && geomScene.Leaves.All(leaf => leaf.Checksum == 0);
    }

    private static float GetWorldZoneTriangleEdgeLimit(Ps2GeomScene geomScene, bool isWorldZoneScene)
    {
        if (!isWorldZoneScene)
            return float.PositiveInfinity;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in geomScene.Leaves.SelectMany(static leaf => leaf.Vertices.Select(static vertex => vertex.Position)))
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var size = max - min;
        var sceneMaxDimension = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (sceneMaxDimension <= 0)
            return float.PositiveInfinity;

        return sceneMaxDimension * 0.10f;
    }

    private static bool ShouldSkipWorldZoneLeaf(Ps2GeomLeaf leaf)
    {
        if (leaf.Vertices.Length < 4)
            return false;

        if (leaf.Vertices.Any(v => v.HasNormal))
            return false;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var restartCount = 0;
        foreach (var vertex in leaf.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
            if (vertex.IsStripRestart)
                restartCount++;
        }

        var size = max - min;
        var maxDimension = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (maxDimension < 1000f)
            return false;

        var center = (min + max) * 0.5f;
        if (Math.Abs(center.X) > 10f || Math.Abs(center.Y) > 10f || Math.Abs(center.Z) > 10f)
            return false;

        return restartCount >= Math.Max(2, leaf.Vertices.Length / 5);
    }

    private static GeomMaterialKey CreateGeomMaterialKey(uint textureChecksum, ulong clamp1, ulong alpha1, ulong test1)
    {
        var clampBits = (byte)(clamp1 & 0x0F);
        var alphaBlend = (byte)(alpha1 & 0xFF);
        var fix = (byte)((alpha1 >> 32) & 0xFF);
        var ate = (test1 & 1) != 0;
        var aref = (byte)((test1 >> 4) & 0xFF);
        return new GeomMaterialKey(textureChecksum, clampBits, alphaBlend, ate ? aref : (byte)0, fix);
    }

    private static MaterialBuilder GetOrCreateGeomMaterial(
        GeomMaterialKey key,
        Dictionary<GeomMaterialKey, MaterialBuilder> cache,
        Ps2SceneGltfWriter.TextureProvider? textureProvider)
    {
        if (cache.TryGetValue(key, out var existing))
            return existing;

        var textureChecksum = key.TextureChecksum;
        var clampBits = key.ClampBits;
        var alphaBlend = key.AlphaBlend;
        var aref = key.AlphaRef;
        var fixValue = key.FixValue;

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
                var intensity = Math.Min(fixValue / 128f, 1f);
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
                var opacity = Math.Min(fixValue / 128f, 1f);
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
                if (fixValue < Ps2SceneGltfWriter.FixBlendOpaqueThreshold)
                {
                    builder.WithAlpha(AlphaMode.BLEND);
                    builder.WithBaseColor(new Vector4(1f, 1f, 1f, fixValue / 128f));
                }
                // else: fix >= threshold -> leave as default OPAQUE
            }
            else
            {
                // Non-FIX blend modes (source-alpha, dest-alpha) -> always BLEND
                builder.WithAlpha(AlphaMode.BLEND);
            }
        }
        else if (aref >= 1)
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
        byte AlphaRef,
        byte FixValue);

    private sealed class GeomMeshBucket(
        string name,
        MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> mesh,
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> primitive,
        HashSet<(Vector3, Vector3, Vector3)> dedup)
    {
        public string Name { get; } = name;
        public MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> Mesh { get; } = mesh;

        public PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> Primitive
        {
            get;
        } = primitive;

        public HashSet<(Vector3, Vector3, Vector3)> Dedup { get; } = dedup;
        public int TriangleCount { get; set; }
    }
}
