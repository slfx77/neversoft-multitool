using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
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

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

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
        return Write(geomScene, outputPath, placements: null, textureProvider, tex0Resolver);
    }

    /// <summary>
    ///     Writes a parsed PS2 GEOM scene to a .glb file with one scene node per placement.
    ///     When <paramref name="placements"/> is null or empty, a single identity-transform node
    ///     is emitted (matches the default <see cref="Write(Ps2GeomScene,string,Ps2SceneGltfWriter.TextureProvider?,Tex0Resolver?)"/> behaviour).
    /// </summary>
    public static int Write(Ps2GeomScene geomScene, string outputPath,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation)>? placements,
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null,
        Tex0Resolver? tex0Resolver = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var triangles = AppendToScene(scene, geomScene, placements, textureProvider, tex0Resolver);
        if (triangles == 0) return 0;

        var model = scene.ToGltf2();
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    /// <summary>
    ///     Appends a parsed PS2 GEOM scene to a SharpGLTF <see cref="SceneBuilder"/>, emitting
    ///     one scene node per placement (or a single identity node when placements is null/empty).
    ///     Use this for combined worldzone .glbs where multiple MDLs are stitched into one scene.
    ///     Optional <paramref name="leafFilter"/> selects which leaves participate; used by the
    ///     worldzone object flow to emit world-space batches and local-space (per-bone) batches
    ///     separately.
    /// </summary>
    public static int AppendToScene(SceneBuilder scene, Ps2GeomScene geomScene,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation)>? placements,
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null,
        Tex0Resolver? tex0Resolver = null,
        Func<Ps2GeomLeaf, bool>? leafFilter = null)
    {
        var (buckets, triangles) = BuildMeshBuckets(geomScene, textureProvider, tex0Resolver, leafFilter);
        if (triangles == 0) return 0;

        var instances = placements is { Count: > 0 }
            ? placements
            : [(Vector3.Zero, Quaternion.Identity)];

        foreach (var bucket in buckets.Values)
        {
            if (bucket.TriangleCount == 0)
                continue;

            for (var i = 0; i < instances.Count; i++)
            {
                var (pos, rot) = instances[i];
                var nodeName = instances.Count == 1
                    ? bucket.Name
                    : $"{bucket.Name}_p{i:D4}";
                var node = new NodeBuilder(nodeName)
                    .WithLocalTranslation(pos)
                    .WithLocalRotation(rot);
                scene.AddRigidMesh(bucket.Mesh, node);
            }
        }

        return triangles;
    }

    internal static (ModelRoot Model, int Triangles) Build(Ps2GeomScene geomScene,
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null,
        Tex0Resolver? tex0Resolver = null)
    {
        var scene = new SceneBuilder();
        var triangles = AppendToScene(scene, geomScene, placements: null, textureProvider, tex0Resolver);
        return (scene.ToGltf2(), triangles);
    }

    private static (Dictionary<GeomMaterialKey, GeomMeshBucket> Buckets, int Triangles) BuildMeshBuckets(
        Ps2GeomScene geomScene,
        Ps2SceneGltfWriter.TextureProvider? textureProvider,
        Tex0Resolver? tex0Resolver,
        Func<Ps2GeomLeaf, bool>? leafFilter = null)
    {
        var materialCache = new Dictionary<GeomMaterialKey, MaterialBuilder>();
        var buckets = new Dictionary<GeomMaterialKey, GeomMeshBucket>();
        var totalTriangles = 0;
        var isWorldZoneScene = IsWorldZoneScene(geomScene);
        var triangleEdgeLimit = GetWorldZoneTriangleEdgeLimit(geomScene, isWorldZoneScene);

        foreach (var leaf in geomScene.Leaves)
        {
            if (leaf.Vertices.Length < 3) continue;

            if (leafFilter != null && !leafFilter(leaf))
                continue;

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

        return (buckets, totalTriangles);
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
            ? QbKey.QbKey.TryResolve(key.TextureChecksum) ?? $"tex_{key.TextureChecksum:X8}"
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
        foreach (var position in geomScene.Leaves.SelectMany(static leaf =>
                     leaf.Vertices.Select(static vertex => vertex.Position)))
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
            ? QbKey.QbKey.TryResolve(textureChecksum) ?? $"tex_{textureChecksum:X8}"
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

        // ALPHA_1 low byte: 0x0A/0x1A/0x00 means PS2 outputs source colour with no
        // alpha contribution (Cv = Cs). When ATE is also off, the alpha channel is
        // entirely irrelevant — the texture renders fully opaque even if the artist
        // baked alpha gradients into the PNG (typical of building wall textures with
        // window highlights). Treating those PNGs as BLEND/MASK based on histogram
        // makes whole buildings translucent.
        //
        // We use this as the "force opaque" signal: if GS would have ignored alpha,
        // bake alpha=255 so glTF viewers do the same.
        var isOpaqueBlend = alphaBlend is 0x0A or 0x1A or 0x00;
        var psIgnoresAlpha = isOpaqueBlend && aref == 0;

        // Standard alpha blend (Cs*As + Cd*(1-As)) with alpha test disabled. PS2
        // titles routinely encoded a per-pixel checker/dither pattern in the alpha
        // channel of window glass and similar surfaces and relied on the low GS
        // resolution + CRT blur to perceptually average it into translucency. In
        // glTF we can't reproduce that smoothing, so a literal MASK at 0.5 renders
        // the dither as a visible checkerboard — see dither investigation in
        // tools/diagnostics/score_dither_textures.py for the empirical separator.
        var isStandardBlend = aField == 0 && bField == 1 && cField == 0 && dField == 1;
        var ditherCandidate = isStandardBlend && aref == 0;

        var alphaProfile = AlphaProfile.AllOpaque;
        if (textureProvider != null && textureChecksum != 0)
        {
            var pngBytes = textureProvider(textureChecksum);
            if (pngBytes != null)
            {
                // Additive / subtractive blend approximations: convert the texture to
                // luminance-alpha first so the profile reflects the final image.
                if (isAdditive)
                    pngBytes = ConvertBlendTexture(pngBytes, 255, 255, 255);
                else if (isSubtractive)
                    pngBytes = ConvertBlendTexture(pngBytes, 0, 0, 0);
                else if (psIgnoresAlpha)
                    pngBytes = ForceAlphaOpaque(pngBytes);
                else if (ditherCandidate && IsDitheredAlpha(pngBytes))
                    pngBytes = ForceAlphaOpaque(pngBytes);

                alphaProfile = AnalyzeAlphaProfile(pngBytes);

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

        // Alpha mode selection.
        //
        // Additive/subtractive get BLEND (texture is already luminance-alpha) and
        // FIX-mode opacity gets BLEND with a base-colour alpha override.
        //
        // Otherwise we use the PNG alpha HISTOGRAM rather than the GS AREF byte:
        //   AllOpaque  -> OPAQUE (no transparent pixels — includes the "PS2 ignores
        //                 alpha" case where we already forced alpha=255 above)
        //   Bimodal    -> MASK at 0.5 (signs / fences / foliage — clean hard cutout)
        //   Graduated  -> BLEND (shadows / decals — soft falloff)
        //
        // This fixes shadow textures that were previously forced into MASK with a
        // pixel-accurate AREF/255 threshold (jagged edge), and sign textures whose
        // graphic was hidden inside a larger transparent quad.
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
        else if (cField == 2 && fixValue < Ps2SceneGltfWriter.FixBlendOpaqueThreshold)
        {
            // Fixed-blend opacity: C field (bits 4-5) == 2 means FIX mode. FIX value in
            // ALPHA_1 bits 32-39; opacity = FIX/128. High FIX values fall through to the
            // histogram path below and typically land on OPAQUE, avoiding z-sort issues.
            builder.WithAlpha(AlphaMode.BLEND);
            builder.WithBaseColor(new Vector4(1f, 1f, 1f, fixValue / 128f));
        }
        else
        {
            switch (alphaProfile)
            {
                case AlphaProfile.Bimodal:
                    builder.WithAlpha(AlphaMode.MASK, 0.5f);
                    break;
                case AlphaProfile.Graduated:
                    builder.WithAlpha(AlphaMode.BLEND);
                    break;
                case AlphaProfile.AllOpaque:
                default:
                    // Leave as default OPAQUE. Nothing to blend against.
                    break;
            }
        }

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    ///     Rewrite a PNG so every pixel has alpha = 255 while preserving RGB. Used for
    ///     materials whose GS ALPHA register yields output = Cs (source colour only)
    ///     with the alpha test disabled — PS2 hardware ignores the alpha channel
    ///     entirely, and we want glTF viewers to render the same way regardless of how
    ///     they handle premultiplication.
    /// </summary>
    private static byte[] ForceAlphaOpaque(byte[] pngBytes)
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
                    row[x] = new Rgba32(p.R, p.G, p.B, (byte)255);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Coarse classification of a PNG's alpha channel into three buckets used for
    ///     glTF alphaMode selection.
    ///     <see cref="AllOpaque"/>: all pixels at or near full opacity.
    ///     <see cref="Bimodal"/>: most pixels are at the extremes (a=0 or a=255) with at
    ///     most a thin antialiasing fringe at the boundaries — a hard-edge cutout
    ///     (fences, foliage, signs, decals over a transparent quad). Maps to MASK so
    ///     the result writes to the depth buffer and occludes correctly.
    ///     <see cref="Graduated"/>: a substantial fraction of pixels fall in the
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
    ///     Scan a PNG's alpha channel and bucket each pixel as low (≤ 8), high (≥ 248),
    ///     or middle. The classification rule is "where is the mass of the histogram?":
    ///     pixels concentrated at the extremes indicate a cutout-with-fringe (Bimodal),
    ///     pixels spread through the mid range indicate a true gradient (Graduated).
    ///     Using mid-fraction alone (the simpler rule) wrongly classifies palm-leaf
    ///     and sign textures as Graduated because of their antialiased edges, which
    ///     then renders as BLEND and bleeds through the depth buffer.
    /// </summary>
    private static AlphaProfile AnalyzeAlphaProfile(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var total = image.Width * image.Height;
        if (total == 0) return AlphaProfile.AllOpaque;

        var counts = new int[3]; // 0=low, 1=high, 2=mid
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var a = row[x].A;
                    if (a <= 8) counts[0]++;
                    else if (a >= 248) counts[1]++;
                    else counts[2]++;
                }
            }
        });
        var low = counts[0];
        var high = counts[1];
        var mid = counts[2];

        // No transparency at all: texture is fully opaque.
        if (low == 0 && mid == 0)
            return AlphaProfile.AllOpaque;

        // Extremes (a=0 + a=255) ≥ 80% of pixels: most of the image is opaque or
        // fully transparent, the rest is antialiasing noise. MASK at 0.5 keeps the
        // hard outline and writes to depth so geometry behind correctly occludes.
        // The 80% threshold is empirical: palm-leaf cutouts come in around 85-95%
        // extremes (5-15% AA fringe); soft shadow textures come in well below 50%.
        if ((low + high) * 5 >= total * 4)
            return AlphaProfile.Bimodal;

        return AlphaProfile.Graduated;
    }

    /// <summary>
    ///     Detect a high-frequency dithered alpha pattern: pixels at extreme alpha
    ///     (a=0 or a=255) that alternate every 1-2 pixels. Returns true when the
    ///     fraction of horizontal-and-vertical neighbour pairs that flip between
    ///     the two extremes exceeds a small threshold.
    ///
    ///     Empirically validated against z_bh.pak.ps2 worldzone textures: window
    ///     glass dithers score 5-28%, while genuine cutouts (chain link fences,
    ///     antialiased silhouettes) score below 4%.
    /// </summary>
    private static bool IsDitheredAlpha(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var w = image.Width;
        var h = image.Height;
        var pairTotal = 2 * w * h - w - h;
        if (pairTotal <= 0) return false;

        var alternations = 0;
        image.ProcessPixelRows(accessor =>
            alternations = CountExtremeAlphaAlternations(accessor));

        // 5% threshold separates dithers from cutouts in the empirical sample.
        return alternations * 20 >= pairTotal;
    }

    private static bool IsExtremeAlphaFlip(byte a1, byte a2) =>
        (a1 == 0 && a2 == 255) || (a1 == 255 && a2 == 0);

    private static int CountExtremeAlphaAlternations(PixelAccessor<Rgba32> accessor)
    {
        var count = 0;
        for (var y = 0; y < accessor.Height; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (var x = 0; x < row.Length - 1; x++)
            {
                if (IsExtremeAlphaFlip(row[x].A, row[x + 1].A))
                    count++;
            }
        }

        for (var y = 0; y < accessor.Height - 1; y++)
        {
            var rowA = accessor.GetRowSpan(y);
            var rowB = accessor.GetRowSpan(y + 1);
            for (var x = 0; x < rowA.Length; x++)
            {
                if (IsExtremeAlphaFlip(rowA[x].A, rowB[x].A))
                    count++;
            }
        }

        return count;
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
