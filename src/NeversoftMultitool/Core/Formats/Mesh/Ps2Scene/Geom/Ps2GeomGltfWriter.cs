using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
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
public static partial class Ps2GeomGltfWriter
{
    private const float WorldzoneBlendOverlayDepthBias = 0.005f;
    private const float WorldzoneMaskCutoutDepthBias = 0.010f;

    /// <summary>
    ///     Per-render-group depth bias spacing (PS2 units). Each render group
    ///     gets pushed forward by <c>group_index * spacing</c> along the surface
    ///     normal so the deepest group still wins the depth test under glTF
    ///     viewers that don't emulate the engine's draw-order layering. Real
    ///     group keys range 1..200ish on z_bh, so 0.002 yields a max total
    ///     offset of ~0.4 PS2 units (≈ 1 cm at game scale) — well below
    ///     visual perception at typical worldzone view distances and well
    ///     above 24-bit depth-buffer precision so coplanar decals don't
    ///     Z-fight. The PCSX2 GS dump confirms the engine itself never
    ///     spatially shifts overlays; on PS2 the layering is purely a
    ///     draw-order + ZTST=ZGREATER artifact, which we approximate here
    ///     with a vanishingly small spatial offset.
    /// </summary>
    private const float WorldzoneRenderGroupSpacing = 0.002f;

    private const float WorldzoneSoftShadowAlphaScale = 0.35f;

    /// <summary>
    ///     Alpha-scale factor applied to the luminance-alpha texture produced for
    ///     PS2 SUBTRACTIVE blends (`A=0, B=Cs, D=Cd` → `Cd - Cs·As/128`). The exact
    ///     equation cannot be reproduced in glTF (which has only proportional
    ///     blend); the converted texture's alpha already encodes "darkening
    ///     intensity by source luminance", but the proportional approximation
    ///     reads about 2-3× stronger than the engine. 0.30 brings dirt overlays
    ///     and similar surfaces back into the in-game perceptual range.
    /// </summary>
    private const float WorldzoneSubtractiveAlphaScale = 0.30f;

    private const int GltfRepeatWrap = 10497;

    /// <summary>
    ///     True when a leaf's vertex colours should be pre-baked into its texture
    ///     (Option B for plant/foliage/static-mesh draws). The bake replaces the
    ///     per-vertex modulation with a per-leaf flat tint applied directly to
    ///     the source pixels in PS2-style 8-bit math (clamped to 255). This sidesteps
    ///     glTF viewers' gamma-correct vertex-modulation pass, which over-amplifies
    ///     the slight blue cast typical of sky-light AO bakes.
    ///     Eligibility:
    ///     - Worldzone scenes only (skinned meshes preserve smooth shading).
    ///     - OPAQUE blend mode (`0x0A` / `0x1A` / `0x00`) — the leaf is rendered
    ///     once with no destination blend, so flattening per-vertex variation
    ///     loses the least.
    ///     - The leaf carries non-uniform / non-neutral vertex colours — leaves
    ///     whose vertex colours are all close to (128,128,128) modulate the
    ///     texture by ~1.0 either way; baking is a no-op so we skip.
    ///     - Per-vertex colour range is tight (max - min ≤ 24 in any channel).
    ///     Wider ranges indicate large surfaces with smoothly-varying AO bake
    ///     (ground planes, walls); flattening those to a per-leaf average
    ///     darkens lit areas and washes out shading. Only leaves with
    ///     tightly-clustered vertex colours — typical of foliage cards and
    ///     small props — get baked.
    /// </summary>
    private const int VertexTintBakeMaxChannelRange = 24;

    /// <summary>
    ///     Writes a parsed PS2 GEOM scene to a .glb file.
    ///     GEOM leaves have texture checksums but no full material data.
    ///     Material properties (clamp, alpha) are extracted from DMA chain GS registers.
    ///     For THPS4 files where texture_checksum is 0, the tex0Resolver maps
    ///     DMA chain TEX0 register values to texture checksums via VRAM simulation.
    /// </summary>
    public static int Write(Ps2GeomScene geomScene, string outputPath,
        MeshChecksumTextureResolver? textureProvider = null,
        Ps2Tex0ChecksumResolver? tex0Resolver = null,
        Ps2GeomDebugCollector? debugCollector = null)
    {
        return Write(geomScene, outputPath, null, textureProvider, tex0Resolver, debugCollector);
    }

    /// <summary>
    ///     Writes a parsed PS2 GEOM scene to a .glb file with one scene node per placement.
    ///     When <paramref name="placements" /> is null or empty, a single identity-transform node
    ///     is emitted, matching the default single-node writer behavior.
    /// </summary>
    public static int Write(Ps2GeomScene geomScene, string outputPath,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation)>? placements,
        MeshChecksumTextureResolver? textureProvider = null,
        Ps2Tex0ChecksumResolver? tex0Resolver = null,
        Ps2GeomDebugCollector? debugCollector = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var triangles = AppendToScene(scene, geomScene, placements, textureProvider, tex0Resolver,
            debugCollector: debugCollector);
        if (triangles == 0) return 0;

        var model = scene.ToGltf2();
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        EnsureExplicitTextureSamplers(outputPath);
        return triangles;
    }

    internal static void EnsureExplicitTextureSamplers(string glbPath)
    {
        var file = File.ReadAllBytes(glbPath);
        if (file.Length < 20
            || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0)) != 0x46546C67u
            || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)) != 2)
        {
            return;
        }

        var chunks = ReadGlbChunks(file);
        var jsonChunkIndex = chunks.FindIndex(static chunk => chunk.Type == 0x4E4F534Au);
        if (jsonChunkIndex < 0)
            return;

        var json = Encoding.UTF8.GetString(chunks[jsonChunkIndex].Data).TrimEnd('\0', ' ', '\r', '\n', '\t');
        var root = JsonNode.Parse(json)?.AsObject();
        if (root == null)
            return;

        if (root["textures"] is not JsonArray textures || textures.Count == 0)
            return;

        var changed = false;
        var samplers = root["samplers"] as JsonArray;
        if (samplers == null)
        {
            samplers = [];
            root["samplers"] = samplers;
            changed = true;
        }

        foreach (var samplerNode in samplers)
        {
            if (samplerNode is not JsonObject sampler)
                continue;

            if (!sampler.ContainsKey("wrapS"))
            {
                sampler["wrapS"] = GltfRepeatWrap;
                changed = true;
            }

            if (!sampler.ContainsKey("wrapT"))
            {
                sampler["wrapT"] = GltfRepeatWrap;
                changed = true;
            }
        }

        var repeatSamplerIndex = FindRepeatSampler(samplers);
        if (repeatSamplerIndex < 0)
        {
            repeatSamplerIndex = samplers.Count;
            samplers.Add(new JsonObject
            {
                ["wrapS"] = GltfRepeatWrap,
                ["wrapT"] = GltfRepeatWrap
            });
            changed = true;
        }

        foreach (var textureNode in textures)
        {
            if (textureNode is JsonObject texture && !texture.ContainsKey("sampler"))
            {
                texture["sampler"] = repeatSamplerIndex;
                changed = true;
            }
        }

        if (!changed)
            return;

        chunks[jsonChunkIndex] = new GlbChunk(0x4E4F534Au, Encoding.UTF8.GetBytes(root.ToJsonString()));
        WriteGlbChunks(glbPath, chunks);
    }

    private static int FindRepeatSampler(JsonArray samplers)
    {
        for (var i = 0; i < samplers.Count; i++)
        {
            if (samplers[i] is not JsonObject sampler)
                continue;

            var wrapS = sampler["wrapS"]?.GetValue<int>() ?? GltfRepeatWrap;
            var wrapT = sampler["wrapT"]?.GetValue<int>() ?? GltfRepeatWrap;
            if (wrapS == GltfRepeatWrap
                && wrapT == GltfRepeatWrap)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<GlbChunk> ReadGlbChunks(byte[] file)
    {
        var chunks = new List<GlbChunk>();
        var offset = 12;
        while (offset + 8 <= file.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset)));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4));
            offset += 8;
            if (length < 0 || offset + length > file.Length)
                break;

            chunks.Add(new GlbChunk(type, file.AsSpan(offset, length).ToArray()));
            offset += length;
        }

        return chunks;
    }

    private static void WriteGlbChunks(string glbPath, IReadOnlyList<GlbChunk> chunks)
    {
        var totalLength = 12;
        foreach (var chunk in chunks)
            totalLength += 8 + Align4(chunk.Data.Length);

        using var output = File.Create(glbPath);
        using var writer = new BinaryWriter(output, Encoding.UTF8, false);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)totalLength);

        foreach (var chunk in chunks)
        {
            var paddedLength = Align4(chunk.Data.Length);
            writer.Write((uint)paddedLength);
            writer.Write(chunk.Type);
            writer.Write(chunk.Data);
            var padByte = chunk.Type == 0x4E4F534Au ? (byte)0x20 : (byte)0x00;
            for (var i = chunk.Data.Length; i < paddedLength; i++)
                writer.Write(padByte);
        }
    }

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }

    /// <summary>
    ///     Appends a parsed PS2 GEOM scene to a SharpGLTF <see cref="SceneBuilder" />, emitting
    ///     one scene node per placement (or a single identity node when placements is null/empty).
    ///     Use this for combined worldzone .glbs where multiple MDLs are stitched into one scene.
    ///     Optional <paramref name="leafFilter" /> selects which leaves participate; used by the
    ///     worldzone object flow to emit world-space batches and local-space (per-bone) batches
    ///     separately.
    /// </summary>
    public static int AppendToScene(SceneBuilder scene, Ps2GeomScene geomScene,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation)>? placements,
        MeshChecksumTextureResolver? textureProvider = null,
        Ps2Tex0ChecksumResolver? tex0Resolver = null,
        Func<Ps2GeomLeaf, bool>? leafFilter = null,
        Ps2GeomDebugCollector? debugCollector = null,
        bool localizeMeshOrigins = false,
        float coordinateScale = 1f,
        Ps2TexaTextureResolver? texaTextureProvider = null)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Coordinate scale must be a finite positive value.");

        var (buckets, triangles) = BuildMeshBuckets(
            geomScene,
            ResolveTexaAwareProvider(textureProvider, texaTextureProvider),
            tex0Resolver,
            leafFilter,
            debugCollector);
        if (triangles == 0) return 0;

        var instances = placements is { Count: > 0 }
            ? placements
            : [(Vector3.Zero, Quaternion.Identity)];

        foreach (var bucket in buckets.Values)
        {
            if (bucket.TriangleCount == 0)
                continue;

            var localOrigin = Vector3.Zero;
            if (localizeMeshOrigins && bucket.TryGetCenter(out localOrigin))
            {
                bucket.Mesh.TransformVertices(vertex =>
                {
                    var geometry = vertex.Geometry;
                    geometry.Position = (geometry.Position - localOrigin) * coordinateScale;
                    vertex.Geometry = geometry;
                    return vertex;
                });
            }
            else if (MathF.Abs(coordinateScale - 1f) > 1e-6f)
            {
                bucket.Mesh.TransformVertices(vertex =>
                {
                    var geometry = vertex.Geometry;
                    geometry.Position *= coordinateScale;
                    vertex.Geometry = geometry;
                    return vertex;
                });
            }

            for (var i = 0; i < instances.Count; i++)
            {
                var (pos, rot) = instances[i];
                var nodeName = instances.Count == 1
                    ? bucket.Name
                    : $"{bucket.Name}_p{i:D4}";
                var nodePosition = localizeMeshOrigins
                    ? pos + Vector3.Transform(localOrigin, rot)
                    : pos;
                nodePosition *= coordinateScale;
                var node = new NodeBuilder(nodeName)
                    .WithLocalTranslation(nodePosition)
                    .WithLocalRotation(rot);
                scene.AddRigidMesh(bucket.Mesh, node);
            }
        }

        return triangles;
    }

    internal static int AppendLeafIdDebugScene(
        SceneBuilder scene,
        Ps2GeomScene geomScene,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation)>? placements,
        Func<Ps2GeomLeaf, bool>? leafFilter,
        string mdlName,
        IList<Ps2GeomLeafIdDebugRecord> records,
        ref int nextId,
        Ps2Tex0ChecksumResolver? tex0Resolver = null,
        Func<ulong, uint, Ps2GeomTextureResolution>? debugTextureResolver = null,
        float coordinateScale = 1f)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Coordinate scale must be a finite positive value.");

        var instances = placements is { Count: > 0 }
            ? placements
            : [(Vector3.Zero, Quaternion.Identity)];
        var isWorldZoneScene = IsWorldZoneScene(geomScene);
        var totalTriangles = 0;

        for (var leafIndex = 0; leafIndex < geomScene.Leaves.Count; leafIndex++)
        {
            var leaf = geomScene.Leaves[leafIndex];
            if (leaf.Vertices.Length < 3)
                continue;
            if (leafFilter != null && !leafFilter(leaf))
                continue;
            if (isWorldZoneScene && ShouldSkipWorldZoneLeaf(leaf))
                continue;

            var id = nextId++;
            var color = DebugIdColor(id);
            var colorHex = DebugColorHex(color);
            var resolution = ResolveDebugTextureResolution(leaf, tex0Resolver, debugTextureResolver);
            var materialName = DebugMaterialName(resolution.Checksum, id);
            var debugName = $"leafid_{id:D5}_{materialName}";
            var material = new MaterialBuilder(debugName)
                .WithUnlitShader()
                .WithBaseColor(color)
                .WithDoubleSide(true);
            var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1, VertexEmpty>(debugName);
            var primitive = mesh.UsePrimitive(material);
            var (localMin, localMax) = ComputeBbox(leaf.Vertices);
            var localOrigin = (localMin + localMax) * 0.5f;
            var tris = AddDebugTriangleStrip(
                primitive,
                leaf.Vertices,
                localOrigin,
                coordinateScale,
                isWorldZoneScene);
            if (tris == 0)
                continue;

            for (var placementIndex = 0; placementIndex < instances.Count; placementIndex++)
            {
                var (pos, rot) = instances[placementIndex];
                var nodeName = instances.Count == 1
                    ? debugName
                    : $"{debugName}_p{placementIndex:D4}";
                var nodePosition = pos + Vector3.Transform(localOrigin, rot);
                nodePosition *= coordinateScale;
                var node = new NodeBuilder(nodeName)
                    .WithLocalTranslation(nodePosition)
                    .WithLocalRotation(rot);
                scene.AddRigidMesh(mesh, node);
            }

            totalTriangles += tris;
            var (min, max) = ComputePlacedBbox(leaf.Vertices, instances);
            records.Add(new Ps2GeomLeafIdDebugRecord(
                mdlName,
                leafIndex,
                id,
                colorHex,
                materialName,
                resolution.Checksum,
                leaf.GroupChecksum,
                leaf.DmaTex0,
                leaf.DmaTex1,
                leaf.DmaClamp1,
                leaf.DmaAlpha1,
                leaf.DmaTest1,
                resolution.ResolveMode,
                resolution.SourceLabel,
                resolution.EntryLabel,
                ClassifyWorldzoneRenderLayer(leaf),
                tris,
                instances.Count,
                min,
                max,
                leaf.IsBillboard,
                leaf.IsLocalSpace));
        }

        return totalTriangles;
    }

    internal static (ModelRoot Model, int Triangles) Build(Ps2GeomScene geomScene,
        MeshChecksumTextureResolver? textureProvider = null,
        Ps2Tex0ChecksumResolver? tex0Resolver = null,
        Ps2GeomDebugCollector? debugCollector = null)
    {
        var scene = new SceneBuilder();
        var triangles = AppendToScene(scene, geomScene, null, textureProvider, tex0Resolver,
            debugCollector: debugCollector);
        return (scene.ToGltf2(), triangles);
    }

    /// <summary>
    ///     Wrap whichever provider the caller supplied as a single
    ///     <see cref="Ps2TexaTextureResolver" />. Prefers
    ///     the explicit TEXA-aware one when set; otherwise adapts the legacy
    ///     <see cref="MeshChecksumTextureResolver" /> by ignoring TEXA.
    /// </summary>
    private static Ps2TexaTextureResolver? ResolveTexaAwareProvider(
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider)
    {
        if (texaTextureProvider != null) return texaTextureProvider;
        if (textureProvider == null) return null;
        return (checksum, _) => textureProvider(checksum);
    }

    private static (Dictionary<GeomBucketKey, GeomMeshBucket> Buckets, int Triangles) BuildMeshBuckets(
        Ps2GeomScene geomScene,
        Ps2TexaTextureResolver? textureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        Func<Ps2GeomLeaf, bool>? leafFilter = null,
        Ps2GeomDebugCollector? debugCollector = null)
    {
        var materialCache = new Dictionary<GeomMaterialKey, GeomMaterialInfo>();
        var buckets = new Dictionary<GeomBucketKey, GeomMeshBucket>();
        var syntheticTextures = new Dictionary<uint, byte[]>();
        Ps2TexaTextureResolver? effectiveTextureProvider = textureProvider == null
            ? null
            : (checksum, texa) => syntheticTextures.TryGetValue(checksum, out var syntheticPng)
                ? syntheticPng
                : textureProvider(checksum, texa);
        var recentAlphaMasks = new Dictionary<LeafGeometryKey, DestinationAlphaMaskCandidate>();
        var totalTriangles = 0;
        var isWorldZoneScene = IsWorldZoneScene(geomScene);
        var blendBucketOrdinal = 0;
        var orderedLeaves = GetLeavesInWorldzoneDrawOrder(geomScene.Leaves, isWorldZoneScene);
        var destinationAlphaMasks = BuildDestinationAlphaMaskCandidates(
            orderedLeaves,
            textureProvider,
            tex0Resolver,
            leafFilter,
            isWorldZoneScene);

        foreach (var leaf in orderedLeaves)
        {
            if (leaf.Vertices.Length < 3)
            {
                debugCollector?.AddRejection(MakeLeafRejection(
                    debugCollector.MdlName, "write", "too_few_vertices", -1, leaf));
                continue;
            }

            if (leafFilter != null && !leafFilter(leaf))
                continue;

            if (isWorldZoneScene && ShouldSkipWorldZoneLeaf(leaf))
            {
                debugCollector?.AddRejection(MakeLeafRejection(
                    debugCollector.MdlName, "write", "huge_origin_helper_leaf", -1, leaf));
                continue;
            }

            var texChecksum = leaf.TextureChecksum;
            var resolution = texChecksum != 0
                ? new Ps2GeomTextureResolution(texChecksum, "node_checksum", "", "")
                : new Ps2GeomTextureResolution(0, "untextured", "", "");
            if (texChecksum == 0 && leaf.DmaTex0 != 0 && tex0Resolver != null)
            {
                if (debugCollector?.TextureResolver != null)
                {
                    resolution = debugCollector.TextureResolver(leaf.DmaTex0, leaf.GroupChecksum);
                    texChecksum = resolution.Checksum;
                }
                else
                {
                    texChecksum = tex0Resolver(leaf.DmaTex0, leaf.GroupChecksum);
                    resolution = new Ps2GeomTextureResolution(texChecksum, "tex0_resolver", "", "");
                }
            }

            var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
            var geometryKey = CreateLeafGeometryKey(leaf);
            DestinationAlphaMaskCandidate maskCandidate;
            // THAW_DEST_ALPHA env var — controls C=Ad (destination-alpha) handling.
            //   "synthesize" (default) : current behaviour — find a similar-footprint
            //                            sibling with useful alpha and bake its alpha
            //                            into a synthetic per-mesh texture.
            //   "opaque"               : drop synthesis. C=Ad collapses to "Cs" when
            //                            destination alpha is 1, so emit OPAQUE.
            //   "blend"                : drop synthesis. Treat C=Ad as standard alpha
            //                            blend driven by source alpha; rely on z-order.
            // The synthesis path below is gated on "synthesize" to preserve the
            // existing behaviour for everyone who hasn't opted in.
            var sourceRenderOrder = GetWorldzoneRenderOrderKey(leaf);
            if (textureProvider != null
                && texChecksum != 0
                && UsesDestinationAlphaBlend(alphaBlend)
                && DestAlphaSynthesisEligible()
                && (TryFindDestinationAlphaMask(
                        geometryKey,
                        texChecksum,
                        sourceRenderOrder,
                        destinationAlphaMasks,
                        out maskCandidate)
                    || TryFindRecentAlphaMask(
                        geometryKey,
                        recentAlphaMasks,
                        out maskCandidate))
                && maskCandidate.TextureChecksum != 0)
            {
                var maskChecksum = maskCandidate.TextureChecksum;
                // Each side of the synthesis pulls texture data with its own
                // per-leaf TEXA so PSMCT16/PSMCT24/16-bit-CLUT alpha expansion
                // matches what the GS sees at that leaf's draw time.
                var sourcePng = textureProvider(texChecksum, leaf.DmaTexa);
                var maskPng = textureProvider(maskChecksum, maskCandidate.Leaf.DmaTexa);
                if (sourcePng != null && maskPng != null)
                {
                    // The mask sibling's effective per-pixel alpha at consumer
                    // draw time depends on what kind of layer it is:
                    //   • Opaque writer (alpha1 ∈ {0x0A,0x1A,0x00}): the GS
                    //     wrote framebuffer alpha=1 across the whole bbox.
                    //     The texture's own alpha is irrelevant — use a flat
                    //     full-opacity mask so the consumer renders as-is.
                    //     (This is what the C1B740CA grass really wants — its
                    //     opaque base 34623305 wrote alpha=1 everywhere; the
                    //     decorative-blend overlay's alpha pattern shouldn't
                    //     leak into the consumer.)
                    //   • Heavily-tiled noise mask (e.g. 8×8 stretched 400×):
                    //     mipmap+filter would average the per-texel noise to
                    //     a near-uniform tint, so flatten to the mean alpha.
                    //   • Single-instance shape mask (≤4× tiling): keep the
                    //     full per-texel alpha — it's a real silhouette.
                    var maskAlphaBlend = (byte)(maskCandidate.Leaf.DmaAlpha1 & 0xFF);
                    var maskIsOpaqueWriter = maskAlphaBlend is 0x0A or 0x1A or 0x00;
                    var effectiveMaskPng = maskIsOpaqueWriter
                        ? CreateUniformOpaqueMask()
                        : MaskShouldFlattenToAverage(maskPng, maskCandidate.Leaf)
                            ? FlattenMaskAlphaToAverage(maskPng)
                            : maskPng;
                    var hasUvTransform = TryComputeDestinationAlphaUvTransform(
                        leaf,
                        maskCandidate.Leaf,
                        out var maskFromSourceUv);
                    var transform = hasUvTransform ? maskFromSourceUv : (UvAffineTransform?)null;
                    var maskedPng = ApplyDestinationAlphaMask(
                        sourcePng,
                        effectiveMaskPng,
                        transform,
                        maskCandidate.Leaf.DmaClamp1,
                        leaf.DmaTest1);
                    var syntheticChecksum = CreateSyntheticTextureChecksum(texChecksum, maskChecksum);
                    while (syntheticTextures.TryGetValue(syntheticChecksum, out var existing)
                           && !existing.SequenceEqual(maskedPng))
                    {
                        syntheticChecksum++;
                    }

                    syntheticTextures[syntheticChecksum] = maskedPng;
                    var maskResolveMode = hasUvTransform ? "dest_alpha_uv_mask" : "dest_alpha_mask";
                    resolution = new Ps2GeomTextureResolution(
                        syntheticChecksum,
                        $"{resolution.ResolveMode}+{maskResolveMode}:{maskChecksum:X8}",
                        resolution.SourceLabel,
                        resolution.EntryLabel);
                    texChecksum = syntheticChecksum;
                }
            }

            if (isWorldZoneScene
                && texChecksum != 0
                && IsLikelyStandaloneAlphaMaskLayer(
                    leaf,
                    alphaBlend,
                    effectiveTextureProvider?.Invoke(texChecksum, leaf.DmaTexa)))
            {
                debugCollector?.AddRejection(MakeLeafRejection(
                    debugCollector.MdlName, "write", "standalone_alpha_mask_layer", -1, leaf));
                continue;
            }

            // For OPAQUE worldzone leaves carrying baked AO/lighting in vertex
            // colours, pre-modulate the texture by the leaf's average vertex
            // tint in PS2-style 8-bit math instead of relying on per-vertex
            // modulation in the viewer. glTF viewers gamma-correct vertex
            // colour multiplication, which over-amplifies the slight blue cast
            // typical of sky-light AO bakes (B usually 1-2 above R/G in
            // shadowed verts), making plants/foliage read as dim and bluish.
            var bakedTint = ShouldBakeVertexTint(isWorldZoneScene, leaf, alphaBlend)
                ? ComputeBakedVertexTint(leaf.Vertices)
                : 0u;
            var key = CreateGeomMaterialKey(
                texChecksum,
                leaf.DmaClamp1,
                leaf.DmaAlpha1,
                leaf.DmaTest1,
                leaf.IsBillboard,
                ShouldPreferBlendFromVertexAlpha(leaf, alphaBlend),
                bakedTint);
            var material = GetOrCreateGeomMaterial(key, materialCache, effectiveTextureProvider, leaf.DmaTexa);
            var bucket = GetOrCreateBucket(
                key,
                material,
                buckets,
                ShouldUseUniqueWorldzoneBlendBucket(isWorldZoneScene, leaf, material.AlphaMode)
                    ? ++blendBucketOrdinal
                    : 0);
            var depthBias = ComputeWorldzoneMaterialDepthBias(isWorldZoneScene, leaf, bucket.AlphaMode);
            var vertices = depthBias > 0f
                ? OffsetVertices(leaf.Vertices, ComputeOverlayOffsetDirection(leaf.Vertices), depthBias)
                : leaf.Vertices;
            var tris = Ps2SceneGltfWriter.AddTriangleStrip(bucket.Primitive, vertices,
                dedup: bucket.Dedup,
                resetOnRestart: isWorldZoneScene,
                preserveVertexAlpha: bucket.PreserveVertexAlpha,
                bakeVertexColorsToWhite: bucket.BakeVertexColorsToWhite);

            if (tris == 0) continue;

            totalTriangles += tris;
            bucket.TriangleCount += tris;
            bucket.Include(vertices);
            debugCollector?.AddMaterial(MakeMaterialDebugRecord(
                debugCollector.MdlName, bucket, leaf, texChecksum, resolution, tris));

            if (textureProvider != null
                && texChecksum != 0
                && !UsesDestinationAlphaBlend(alphaBlend))
            {
                var candidate = new DestinationAlphaMaskCandidate(geometryKey, texChecksum, leaf);
                recentAlphaMasks[geometryKey] = candidate;
            }
        }

        return (buckets, totalTriangles);
    }

    private static IReadOnlyList<Ps2GeomLeaf> GetLeavesInWorldzoneDrawOrder(
        IReadOnlyList<Ps2GeomLeaf> leaves,
        bool isWorldZoneScene)
    {
        if (!isWorldZoneScene || leaves.Count <= 1)
            return leaves;

        return leaves
            .Select(static (leaf, index) => (leaf, index))
            .OrderBy(static item => GetWorldzoneRenderOrderKey(item.leaf))
            .ThenBy(static item => item.index)
            .Select(static item => item.leaf)
            .ToArray();
    }

    internal static uint GetWorldzoneRenderOrderKey(Ps2GeomLeaf leaf)
    {
        // THAW level-MDL preamble +0x4C is a 1-based material/render group.
        // The VIF chunks are laid out by data offset, not final draw pass; exact
        // coplanar wall/ground overlays rely on these groups drawing after their base.
        if (leaf.GroupChecksum is > 0 and <= 0xFF)
            return leaf.GroupChecksum;

        // Fallback ordering when the level-MDL preamble +0x4C field is missing
        // (synthetic test data, non-worldzone geom). Matches the THAW PS2 draw
        // pattern observed in real worldzones:
        //   1. Fully-opaque base (0x100)
        //   2. Standard source-alpha overlays / "mask carriers" (0x200) —
        //      these write framebuffer alpha that subsequent C=Ad consumers
        //      will read back.
        //   3. Destination-alpha consumers (C=Ad, 0x300) — drawn after the
        //      mask carriers so the framebuffer alpha is in place.
        //   4. Anything else (0x400).
        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        if (alphaBlend is 0x0A or 0x1A or 0x00)
            return 0x0100;
        if (IsStandardSourceAlphaBlend(alphaBlend))
            return 0x0200;
        if (UsesDestinationAlphaBlend(alphaBlend))
            return 0x0300;
        return 0x0400;
    }

    private static List<DestinationAlphaMaskCandidate> BuildDestinationAlphaMaskCandidates(
        IReadOnlyList<Ps2GeomLeaf> leaves,
        Ps2TexaTextureResolver? textureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        Func<Ps2GeomLeaf, bool>? leafFilter,
        bool isWorldZoneScene)
    {
        if (textureProvider == null)
            return [];

        var hasDestinationAlphaLayer = leaves.Any(static leaf =>
            UsesDestinationAlphaBlend((byte)(leaf.DmaAlpha1 & 0xFF)));
        if (!hasDestinationAlphaLayer)
            return [];

        var candidates = new List<DestinationAlphaMaskCandidate>();
        foreach (var leaf in leaves)
        {
            if (leaf.Vertices.Length < 3)
                continue;
            if (leafFilter != null && !leafFilter(leaf))
                continue;
            if (isWorldZoneScene && ShouldSkipWorldZoneLeaf(leaf))
                continue;

            var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
            if (UsesDestinationAlphaBlend(alphaBlend))
                continue;

            var texChecksum = ResolveTextureChecksum(leaf, tex0Resolver);
            if (texChecksum == 0)
                continue;

            var pngBytes = textureProvider(texChecksum, leaf.DmaTexa);
            if (pngBytes == null || !HasUsefulDestinationAlpha(pngBytes))
                continue;

            candidates.Add(new DestinationAlphaMaskCandidate(CreateLeafGeometryKey(leaf), texChecksum, leaf));
        }

        return candidates;
    }

    private static uint ResolveTextureChecksum(Ps2GeomLeaf leaf, Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        if (leaf.TextureChecksum != 0)
            return leaf.TextureChecksum;

        return leaf.DmaTex0 != 0 && tex0Resolver != null
            ? tex0Resolver(leaf.DmaTex0, leaf.GroupChecksum)
            : 0;
    }

    private static Ps2GeomTextureResolution ResolveDebugTextureResolution(
        Ps2GeomLeaf leaf,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        Func<ulong, uint, Ps2GeomTextureResolution>? debugTextureResolver)
    {
        if (leaf.TextureChecksum != 0)
            return new Ps2GeomTextureResolution(leaf.TextureChecksum, "node_checksum", "", "");

        if (leaf.DmaTex0 == 0)
            return new Ps2GeomTextureResolution(0, "untextured", "", "");

        if (debugTextureResolver != null)
            return debugTextureResolver(leaf.DmaTex0, leaf.GroupChecksum);

        if (tex0Resolver != null)
        {
            var checksum = tex0Resolver(leaf.DmaTex0, leaf.GroupChecksum);
            return new Ps2GeomTextureResolution(checksum, checksum != 0 ? "tex0_resolver" : "unresolved_tex0", "", "");
        }

        return new Ps2GeomTextureResolution(0, "unresolved_tex0", "", "");
    }

    private static string DebugMaterialName(uint textureChecksum, int id)
    {
        if (textureChecksum == 0)
            return $"untextured_{id:D5}";

        return QbKey.QbKey.TryResolve(textureChecksum) ?? $"tex_{textureChecksum:X8}";
    }

    private static int AddDebugTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1, VertexEmpty> primitive,
        Ps2Vertex[] vertices,
        Vector3 localOrigin,
        float coordinateScale,
        bool resetOnRestart)
    {
        var count = 0;
        var stripStart = 0;
        var lastWasRestart = false;

        for (var i = 0; i < vertices.Length; i++)
        {
            if (vertices[i].IsStripRestart)
            {
                if (resetOnRestart && !lastWasRestart)
                    stripStart = i;
                lastWasRestart = true;
                continue;
            }

            lastWasRestart = false;

            var localIndex = i - stripStart;
            if (localIndex < 2)
                continue;

            Ps2Vertex a;
            Ps2Vertex b;
            var c = vertices[i];
            if ((localIndex & 1) == 0)
            {
                a = vertices[i - 2];
                b = vertices[i - 1];
            }
            else
            {
                a = vertices[i - 1];
                b = vertices[i - 2];
            }

            if (Ps2SceneGltfWriter.IsDegenerate(a, b, c))
                continue;

            primitive.AddTriangle(
                MakeDebugVertex(a, localOrigin, coordinateScale),
                MakeDebugVertex(b, localOrigin, coordinateScale),
                MakeDebugVertex(c, localOrigin, coordinateScale));
            count++;
        }

        return count;
    }

    private static VertexBuilder<VertexPositionNormal, VertexColor1, VertexEmpty> MakeDebugVertex(
        Ps2Vertex vertex,
        Vector3 localOrigin,
        float coordinateScale)
    {
        var normal = Vector3.UnitY;
        if (vertex.HasNormal)
        {
            var length = vertex.Normal.Length();
            normal = length > 0.001f ? vertex.Normal / length : Vector3.UnitY;
        }

        return new VertexBuilder<VertexPositionNormal, VertexColor1, VertexEmpty>(
            new VertexPositionNormal((vertex.Position - localOrigin) * coordinateScale, normal),
            new VertexColor1(Vector4.One));
    }

    private static (Vector3 Min, Vector3 Max) ComputePlacedBbox(
        IReadOnlyList<Ps2Vertex> vertices,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation)> instances)
    {
        if (vertices.Count == 0)
            return (Vector3.Zero, Vector3.Zero);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (position, rotation) in instances)
        {
            foreach (var vertex in vertices)
            {
                var transformed = Vector3.Transform(vertex.Position, rotation) + position;
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }
        }

        return (min, max);
    }

    private static Vector4 DebugIdColor(int id)
    {
        var hue = (float)(id * 0.6180339887498949 % 1.0);
        return HsvToRgb(hue, 0.78f, 0.95f);
    }

    private static Vector4 HsvToRgb(float hue, float saturation, float value)
    {
        var h = hue * 6f;
        var c = value * saturation;
        var x = c * (1f - MathF.Abs(h % 2f - 1f));
        var m = value - c;

        var (r, g, b) = h switch
        {
            < 1f => (c, x, 0f),
            < 2f => (x, c, 0f),
            < 3f => (0f, c, x),
            < 4f => (0f, x, c),
            < 5f => (x, 0f, c),
            _ => (c, 0f, x)
        };

        return new Vector4(r + m, g + m, b + m, 1f);
    }

    private static string DebugColorHex(Vector4 color)
    {
        static int ToByte(float value)
        {
            return Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
        }

        return $"#{ToByte(color.X):X2}{ToByte(color.Y):X2}{ToByte(color.Z):X2}";
    }

    private static GeomMeshBucket GetOrCreateBucket(
        GeomMaterialKey key,
        GeomMaterialInfo material,
        Dictionary<GeomBucketKey, GeomMeshBucket> buckets,
        int uniqueBlendOrdinal)
    {
        var bucketKey = new GeomBucketKey(key, uniqueBlendOrdinal);
        if (buckets.TryGetValue(bucketKey, out var existing))
            return existing;

        var name = key.TextureChecksum != 0
            ? QbKey.QbKey.TryResolve(key.TextureChecksum) ?? $"tex_{key.TextureChecksum:X8}"
            : $"geom_{buckets.Count:D4}";
        if (uniqueBlendOrdinal != 0)
            name = $"{name}_blend_{uniqueBlendOrdinal:D4}";
        var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(name);
        var bucket = new GeomMeshBucket(name, material.AlphaMode, mesh, mesh.UsePrimitive(material.Material),
            new HashSet<(Vector3, Vector3, Vector3)>(), ShouldPreserveVertexAlpha(key, material.AlphaMode),
            key.BakedVertexTintRgba >> 24 == 0xFFu);
        buckets[bucketKey] = bucket;
        return bucket;
    }

    private static bool IsWorldZoneScene(Ps2GeomScene geomScene)
    {
        return geomScene.Leaves.Count >= 500 && geomScene.Leaves.All(leaf => leaf.Checksum == 0);
    }

    private static Ps2GeomLeafRejection MakeLeafRejection(
        string mdlName,
        string stage,
        string reason,
        int leafIndex,
        Ps2GeomLeaf leaf)
    {
        var (min, max) = ComputeBbox(leaf.Vertices);
        return new Ps2GeomLeafRejection(
            mdlName,
            stage,
            reason,
            leafIndex,
            leaf.Vertices.Length,
            leaf.DmaTex0,
            min,
            max);
    }

    private static Ps2GeomMaterialDebugRecord MakeMaterialDebugRecord(
        string mdlName,
        GeomMeshBucket bucket,
        Ps2GeomLeaf leaf,
        uint textureChecksum,
        Ps2GeomTextureResolution resolution,
        int triangles)
    {
        var (min, max) = ComputeBbox(leaf.Vertices);
        return new Ps2GeomMaterialDebugRecord(
            mdlName,
            bucket.Name,
            textureChecksum,
            leaf.GroupChecksum,
            leaf.DmaTex0,
            leaf.DmaTex1,
            leaf.DmaClamp1,
            leaf.DmaAlpha1,
            leaf.DmaTest1,
            bucket.AlphaMode,
            resolution.ResolveMode,
            resolution.SourceLabel,
            resolution.EntryLabel,
            ClassifyWorldzoneRenderLayer(leaf),
            triangles,
            min,
            max,
            leaf.IsBillboard);
    }

    private static (Vector3 Min, Vector3 Max) ComputeBbox(IReadOnlyList<Ps2Vertex> vertices)
    {
        if (vertices.Count == 0)
            return (Vector3.Zero, Vector3.Zero);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return (min, max);
    }

    private static LeafGeometryKey CreateLeafGeometryKey(Ps2GeomLeaf leaf)
    {
        var (min, max) = ComputeBbox(leaf.Vertices);
        return new LeafGeometryKey(leaf.Vertices.Length, min, max);
    }

    /// <summary>
    ///     Live-iteration fallback: match against <b>exactly</b> the same bbox
    ///     among recently-processed leaves. The render-order constraint is
    ///     implicit here — these candidates were added as we walked the
    ///     render-order-sorted leaf list, so they were drawn earlier by
    ///     definition. Approximate-footprint matching is intentionally absent
    ///     to avoid the false positives that motivated the rewrite of
    ///     <see cref="TryFindDestinationAlphaMask" />.
    /// </summary>
    private static bool TryFindRecentAlphaMask(
        LeafGeometryKey geometryKey,
        Dictionary<LeafGeometryKey, DestinationAlphaMaskCandidate> exactMasks,
        out DestinationAlphaMaskCandidate maskCandidate)
    {
        if (exactMasks.TryGetValue(geometryKey, out maskCandidate))
            return true;

        maskCandidate = default;
        return false;
    }

    /// <summary>
    ///     Find the destination-alpha mask candidate for a C=Ad consumer leaf.
    ///     Restricted to <b>exact</b> bbox match AND siblings drawn <b>earlier</b>
    ///     in render order — this matches the THAW PS2 framebuffer-alpha mechanic
    ///     where an earlier same-bbox draw establishes the alpha pattern that the
    ///     C=Ad consumer reads back.
    /// </summary>
    /// <remarks>
    ///     Selection is layered:
    ///     <list type="number">
    ///         <item>
    ///             Prefer the latest <b>opaque</b> sibling (alpha1 ∈
    ///             {0x0A,0x1A,0x00}, no blend). PS2 GS opaque draws write alpha=1
    ///             to the framebuffer, so the C=Ad consumer reads a uniform mask
    ///             covering the whole bbox. The mask leaf is returned but the
    ///             caller treats its alpha as effectively-uniform-opaque (the
    ///             texture's own per-texel alpha doesn't reach the framebuffer).
    ///         </item>
    ///         <item>
    ///             Fall back to the latest blend sibling. Standard alpha-
    ///             blend draws (alpha1=0x44) often skip framebuffer-alpha writes
    ///             via FBMSK, but when a meaningful shape exists in the blend
    ///             texture's alpha (dirt patches, etc.) the consumer is paired
    ///             with that shape as the mask — closest-earlier wins.
    ///         </item>
    ///     </list>
    ///     The legacy approximate-footprint fallback is gone — it caused
    ///     false positives where unrelated decals near the consumer's bbox
    ///     got picked as masks (C1B740CA grass × E6BE2F91 frame-decal).
    /// </remarks>
    private static bool TryFindDestinationAlphaMask(
        LeafGeometryKey geometryKey,
        uint sourceChecksum,
        uint sourceRenderOrder,
        IReadOnlyList<DestinationAlphaMaskCandidate> candidates,
        out DestinationAlphaMaskCandidate maskCandidate)
    {
        DestinationAlphaMaskCandidate? bestOpaque = null;
        uint bestOpaqueOrder = 0;
        DestinationAlphaMaskCandidate? bestBlend = null;
        uint bestBlendOrder = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.TextureChecksum == 0 || candidate.TextureChecksum == sourceChecksum)
                continue;
            if (!geometryKey.Equals(candidate.Geometry))
                continue;
            var candOrder = GetWorldzoneRenderOrderKey(candidate.Leaf);
            if (candOrder >= sourceRenderOrder)
                continue;

            var candAlphaBlend = (byte)(candidate.Leaf.DmaAlpha1 & 0xFF);
            var isOpaqueWriter = candAlphaBlend is 0x0A or 0x1A or 0x00;
            if (isOpaqueWriter)
            {
                if (bestOpaque is null || candOrder >= bestOpaqueOrder)
                {
                    bestOpaque = candidate;
                    bestOpaqueOrder = candOrder;
                }
            }
            else
            {
                if (bestBlend is null || candOrder >= bestBlendOrder)
                {
                    bestBlend = candidate;
                    bestBlendOrder = candOrder;
                }
            }
        }

        // Opaque wins (it actually writes framebuffer alpha=1). Fall back to
        // blend siblings only when no opaque candidate exists.
        if (bestOpaque is { } foundOpaque)
        {
            maskCandidate = foundOpaque;
            return true;
        }

        if (bestBlend is { } foundBlend)
        {
            maskCandidate = foundBlend;
            return true;
        }

        maskCandidate = default;
        return false;
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

    private static bool IsLikelyStandaloneAlphaMaskLayer(
        Ps2GeomLeaf leaf,
        byte alphaBlend,
        byte[]? pngBytes)
    {
        if (pngBytes == null
            || leaf.IsBillboard
            || !IsStandardSourceAlphaBlend(alphaBlend))
        {
            return false;
        }

        var (min, max) = ComputeBbox(leaf.Vertices);
        var size = max - min;
        var maxDimension = Math.Max(Math.Abs(size.X), Math.Max(Math.Abs(size.Y), Math.Abs(size.Z)));
        if (maxDimension < 250f)
            return false;

        return IsLikelyNeutralSparseAlphaMask(pngBytes);
    }

    private static bool IsLikelyNeutralSparseAlphaMask(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var totalPixels = image.Width * image.Height;
        if (totalPixels == 0)
            return false;

        long lowAlphaPixels = 0;
        long highAlphaPixels = 0;
        long midAlphaPixels = 0;
        long alphaWeight = 0;
        long maxChannelWeight = 0;
        long channelRangeWeight = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A <= 8)
                    {
                        lowAlphaPixels++;
                        continue;
                    }

                    if (p.A >= 248)
                        highAlphaPixels++;
                    else
                        midAlphaPixels++;

                    var maxChannel = Math.Max(p.R, Math.Max(p.G, p.B));
                    var minChannel = Math.Min(p.R, Math.Min(p.G, p.B));
                    alphaWeight += p.A;
                    maxChannelWeight += maxChannel * p.A;
                    channelRangeWeight += (maxChannel - minChannel) * p.A;
                }
            }
        });

        if (highAlphaPixels == 0 || alphaWeight == 0)
            return false;
        if ((lowAlphaPixels + highAlphaPixels) * 20 < totalPixels * 19)
            return false;
        if (midAlphaPixels * 20 > totalPixels)
            return false;

        var visibleCoverage = highAlphaPixels / (double)totalPixels;
        if (visibleCoverage is < 0.10 or > 0.45)
            return false;

        var averageMaxChannel = maxChannelWeight / (double)alphaWeight;
        var averageChannelRange = channelRangeWeight / (double)alphaWeight;
        return averageMaxChannel is >= 96.0 and <= 224.0
               && averageChannelRange <= 24.0;
    }

    private static GeomMaterialKey CreateGeomMaterialKey(
        uint textureChecksum,
        ulong clamp1,
        ulong alpha1,
        ulong test1,
        bool preferCutout,
        bool preferBlend,
        uint bakedVertexTintRgba = 0u)
    {
        var clampBits = (byte)(clamp1 & 0x0F);
        var alphaBlend = (byte)(alpha1 & 0xFF);
        var fix = (byte)((alpha1 >> 32) & 0xFF);
        var ate = (test1 & 1) != 0;
        var aref = (byte)((test1 >> 4) & 0xFF);
        return new GeomMaterialKey(
            textureChecksum,
            clampBits,
            alphaBlend,
            ate ? aref : (byte)0,
            fix,
            preferCutout,
            preferBlend,
            bakedVertexTintRgba);
    }

    private static bool ShouldPreferBlendFromVertexAlpha(Ps2GeomLeaf leaf, byte alphaBlend)
    {
        if (leaf.IsBillboard || !IsStandardSourceAlphaBlend(alphaBlend))
            return false;

        return leaf.Vertices.Any(static vertex =>
            vertex.HasColor && vertex.A > 0 && vertex.A < 120);
    }

    private static bool ShouldUseUniqueWorldzoneBlendBucket(
        bool isWorldZoneScene,
        Ps2GeomLeaf leaf,
        string alphaMode)
    {
        return isWorldZoneScene
               && !leaf.IsBillboard
               && string.Equals(alphaMode, "BLEND", StringComparison.Ordinal);
    }

    private static bool ShouldPreserveVertexAlpha(GeomMaterialKey key, string alphaMode)
    {
        if (!string.Equals(alphaMode, "BLEND", StringComparison.Ordinal))
            return false;

        var cField = (key.AlphaBlend >> 4) & 0x03;
        return cField == 0;
    }

    internal static float ComputeWorldzoneMaterialDepthBias(
        bool isWorldZoneScene,
        Ps2GeomLeaf leaf,
        string alphaMode)
    {
        if (!isWorldZoneScene || leaf.IsBillboard)
            return 0f;

        // Mode bias: 0 for OPAQUE (depth-writing base), +2/+3 for BLEND/MASK
        // overlays. OPAQUE always returns 0 — we never shift the base layer.
        var modeBias = alphaMode switch
        {
            "MASK" => WorldzoneMaskCutoutDepthBias,
            "BLEND" => WorldzoneBlendOverlayDepthBias,
            _ => 0f
        };
        if (modeBias <= 0f)
            return 0f;

        // Cumulative per-render-group bias: only meaningful for transparent
        // overlays. The render group comes from the level-MDL preamble +0x4C
        // (1-based material/render group index, 1..255 in practice). Pushing
        // each group forward by N * spacing along the surface normal makes
        // glTF viewers' auto-sort (which orders BLEND primitives by camera
        // distance) reproduce the game's intended back-to-front draw order
        // without per-frame guidance. Leaves without a real group fall back
        // to mode bias only — preserves pre-grouping behaviour for files
        // whose preamble is missing or 0.
        var groupBias = leaf.GroupChecksum is > 0u and <= 0xFFu
            ? leaf.GroupChecksum * WorldzoneRenderGroupSpacing
            : 0f;

        return groupBias + modeBias;
    }

    private static Ps2Vertex[] OffsetVertices(Ps2Vertex[] vertices, Vector3 direction, float distance)
    {
        if (vertices.Length == 0 || distance == 0 || direction.LengthSquared() <= 1e-8f)
            return vertices;

        var offset = direction * distance;
        var result = new Ps2Vertex[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            result[i] = new Ps2Vertex(
                vertex.Position + offset,
                vertex.Normal,
                vertex.R,
                vertex.G,
                vertex.B,
                vertex.A,
                vertex.U,
                vertex.V,
                vertex.HasNormal,
                vertex.HasColor,
                vertex.HasUV,
                vertex.IsStripRestart,
                vertex.BoneIndex0,
                vertex.BoneIndex1,
                vertex.BoneIndex2,
                vertex.BoneWeight0,
                vertex.BoneWeight1,
                vertex.BoneWeight2,
                vertex.HasSkinData);
        }

        return result;
    }

    private static Vector3 ComputeOverlayOffsetDirection(Ps2Vertex[] vertices)
    {
        var normal = Vector3.Zero;
        foreach (var vertex in vertices)
        {
            if (!vertex.HasNormal || vertex.Normal.LengthSquared() <= 1e-8f)
                continue;

            normal += Vector3.Normalize(vertex.Normal);
        }

        if (normal.LengthSquared() <= 1e-8f)
            normal = ComputeStripNormal(vertices);

        if (normal.LengthSquared() <= 1e-8f)
            return Vector3.UnitY;

        normal = Vector3.Normalize(normal);
        if (Math.Abs(normal.Y) > 0.5f && normal.Y < 0)
            normal = -normal;
        return normal;
    }

    private static Vector3 ComputeStripNormal(Ps2Vertex[] vertices)
    {
        var normal = Vector3.Zero;
        var stripStart = 0;
        var lastWasRestart = false;

        for (var i = 0; i < vertices.Length; i++)
        {
            if (vertices[i].IsStripRestart)
            {
                if (!lastWasRestart)
                    stripStart = i;
                lastWasRestart = true;
                continue;
            }

            lastWasRestart = false;
            var localIndex = i - stripStart;
            if (localIndex < 2)
                continue;

            var a = (localIndex & 1) == 0 ? vertices[i - 2].Position : vertices[i - 1].Position;
            var b = (localIndex & 1) == 0 ? vertices[i - 1].Position : vertices[i - 2].Position;
            var c = vertices[i].Position;
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() > 1e-8f)
                normal += Vector3.Normalize(cross);
        }

        return normal;
    }

    public static Ps2GeomRenderLayer ClassifyWorldzoneRenderLayer(Ps2GeomLeaf leaf)
    {
        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;

        // THAW Beverly Hills uses additive static batches for night-time window,
        // streetlight, skyline, and glow overlays. Foliage billboards can share
        // transparent alpha state, so keep synthetic billboards on the base layer.
        var isAdditiveOverlay = aField == 0 && bField == 2 && dField == 1;
        return isAdditiveOverlay && !leaf.IsBillboard
            ? Ps2GeomRenderLayer.NightOverlay
            : Ps2GeomRenderLayer.Base;
    }

}
