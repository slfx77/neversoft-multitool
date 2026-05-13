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
public static class Ps2GeomGltfWriter
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

    private static GeomMaterialInfo GetOrCreateGeomMaterial(
        GeomMaterialKey key,
        Dictionary<GeomMaterialKey, GeomMaterialInfo> cache,
        Ps2TexaTextureResolver? textureProvider,
        ulong texa)
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
        var isStandardBlend = IsStandardSourceAlphaBlend(alphaBlend);
        var ditherCandidate = isStandardBlend && aref == 0;

        var alphaProfile = AlphaProfile.AllOpaque;
        var isDarkBlendOverlay = false;
        var isSoftShadowOverlay = false;
        var isMonochromeAlphaMask = false;
        var isDitherResolved = false;
        var isFoliageCutout = false;
        if (textureProvider != null && textureChecksum != 0)
        {
            var pngBytes = textureProvider(textureChecksum, texa);
            if (pngBytes != null)
            {
                isDarkBlendOverlay = isStandardBlend && !psIgnoresAlpha && IsDarkAlphaOverlay(pngBytes);
                isFoliageCutout = IsLikelyFoliageCutout(pngBytes);
                isSoftShadowOverlay = isStandardBlend
                                      && !psIgnoresAlpha
                                      && !isFoliageCutout
                                      && IsLikelySoftShadowOverlay(pngBytes);
                // A "monochrome alpha mask" is a texture whose visible pixels
                // share a single RGB color (e.g. pure white, pure black) with
                // shape detail entirely encoded in the alpha channel. These
                // textures are used as stencils — the actual rendered colour
                // comes from per-vertex modulation. Forcing them to MASK alpha
                // mode clips the soft alpha gradient at the shape edges, which
                // is what gives shadows their soft falloff. Force BLEND
                // instead so vertex-color × alpha gradient renders smoothly.
                isMonochromeAlphaMask = isStandardBlend
                                        && !psIgnoresAlpha
                                        && !isFoliageCutout
                                        && !isDarkBlendOverlay
                                        && IsMonochromeAlphaMask(pngBytes);
                var forceOpaqueRgbOnlyTexture = isStandardBlend
                                                && !isFoliageCutout
                                                && IsAllTransparentWithUsefulRgb(pngBytes);

                // Additive / subtractive blend approximations: convert the texture to
                // luminance-alpha first so the profile reflects the final image.
                if (isAdditive)
                    pngBytes = ConvertAdditiveBlendTexture(pngBytes);
                else if (isSubtractive)
                {
                    // PS2 subtractive equation: Cd - Cs*As/128 (constant subtraction
                    // proportional to texture brightness). glTF BLEND can only do
                    // Cs*As + Cd*(1-As) (proportional dimming). For typical dirt/cloud
                    // overlays the proportional approximation reads ~2x stronger than
                    // the engine's constant subtraction, producing dark "blobs" where
                    // the engine renders subtle tints. Scale the converted luminance-
                    // alpha down to bring it back into the in-game perceptual range.
                    pngBytes = ConvertBlendTexture(pngBytes, 0, 0, 0);
                    pngBytes = ScaleTextureAlpha(pngBytes, WorldzoneSubtractiveAlphaScale);
                }
                else if (psIgnoresAlpha)
                    pngBytes = ForceAlphaOpaque(pngBytes);
                else if (forceOpaqueRgbOnlyTexture)
                    pngBytes = ForceAlphaOpaque(pngBytes);
                else if (ditherCandidate && IsDitheredAlpha(pngBytes))
                {
                    pngBytes = ResolveDitheredAlpha(pngBytes);
                    isDitherResolved = true;
                }
                else if (isSoftShadowOverlay)
                {
                    pngBytes = ScaleTextureAlpha(pngBytes, WorldzoneSoftShadowAlphaScale);
                }

                // Per-leaf vertex-tint bake (Option B for OPAQUE worldzone draws):
                // when the leaf carries a non-zero baked tint, multiply the texture
                // by that tint in PS2-style 8-bit math (clamped to 255). The vertex
                // attribute is then emitted as (1,1,1,1) so the result is no longer
                // gamma-amplified by viewers' linear-space vertex modulation.
                if (key.BakedVertexTintRgba >> 24 == 0xFFu)
                {
                    var tintR = (byte)((key.BakedVertexTintRgba >> 16) & 0xFFu);
                    var tintG = (byte)((key.BakedVertexTintRgba >> 8) & 0xFFu);
                    var tintB = (byte)(key.BakedVertexTintRgba & 0xFFu);
                    pngBytes = ModulateTextureBy8BitTint(pngBytes, tintR, tintG, tintB);
                }

                alphaProfile = AnalyzeAlphaProfile(pngBytes);

                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);

                // Set texture wrap mode from CLAMP_1 register.
                // WMS/WMT: 0=REPEAT, 1=CLAMP. Only simple modes used in practice.
                // Emit REPEAT explicitly too; some Windows viewers do not reliably apply
                // glTF's default repeat sampler to large worldzone UV ranges.
                var wms = clampBits & 0x03;
                var wmt = (clampBits >> 2) & 0x03;
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
        // C=Ad (destination-alpha-blend) override path. Strategies via
        // THAW_DEST_ALPHA: "opaque" collapses the GS equation to Cs (assuming
        // destination alpha was 1 from a prior opaque pass); "blend" emits a
        // normal source-alpha BLEND when no exact-bbox sibling synthesized
        // a mask (the synthesis path runs upstream and stamps a synthetic
        // checksum with bit 31 set — when that's our texture, the synthesis
        // baked the mask in already and the override would replace it).
        var destAlphaOverride = DestAlphaOverrideForCField(cField);
        var isSyntheticDestAlphaTexture = (textureChecksum & 0x80000000u) != 0u;
        var alphaModeName = "OPAQUE";
        if (destAlphaOverride is { } overrideMode && !isSyntheticDestAlphaTexture)
        {
            switch (overrideMode)
            {
                case DestAlphaOverride.Opaque:
                    // alpha mode stays OPAQUE; force the texture's alpha to 255 so
                    // glTF doesn't drop the texture in BLEND-style sorting.
                    if (textureProvider != null && textureChecksum != 0)
                    {
                        var pngBytes = textureProvider(textureChecksum, texa);
                        if (pngBytes != null)
                        {
                            var forced = ForceAlphaOpaque(pngBytes);
                            builder.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(forced));
                        }
                    }

                    var info0 = new GeomMaterialInfo(builder, alphaModeName);
                    cache[key] = info0;
                    return info0;
                case DestAlphaOverride.Blend:
                    builder.WithAlpha(AlphaMode.BLEND);
                    alphaModeName = "BLEND";
                    var infoB = new GeomMaterialInfo(builder, alphaModeName);
                    cache[key] = infoB;
                    return infoB;
            }
        }

        if (isAdditive)
        {
            builder.WithAlpha(AlphaMode.BLEND);
            alphaModeName = "BLEND";

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
            alphaModeName = "BLEND";

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
            alphaModeName = "BLEND";
            builder.WithBaseColor(new Vector4(1f, 1f, 1f, fixValue / 128f));
        }
        else if ((key.PreferCutout || isFoliageCutout) && alphaProfile != AlphaProfile.AllOpaque)
        {
            builder.WithAlpha(AlphaMode.MASK);
            alphaModeName = "MASK";
        }
        else if ((key.PreferBlend && alphaProfile != AlphaProfile.Bimodal)
                 || isDarkBlendOverlay
                 || isSoftShadowOverlay
                 || isMonochromeAlphaMask
                 || isDitherResolved)
        {
            // Shadow/decal overlays and low vertex-alpha worldzone cards are authored for GS
            // alpha blending. Exporting them as MASK makes the overlay look like opaque black
            // geometry; keep them as BLEND while foliage/sign cutouts stay on the histogram
            // path below.
            builder.WithAlpha(AlphaMode.BLEND);
            alphaModeName = "BLEND";
        }
        else
        {
            switch (alphaProfile)
            {
                case AlphaProfile.Bimodal:
                    builder.WithAlpha(AlphaMode.MASK);
                    alphaModeName = "MASK";
                    break;
                case AlphaProfile.Graduated:
                    builder.WithAlpha(AlphaMode.BLEND);
                    alphaModeName = "BLEND";
                    break;
                case AlphaProfile.AllOpaque:
                default:
                    // Leave as default OPAQUE. Nothing to blend against.
                    break;
            }
        }

        var info = new GeomMaterialInfo(builder, alphaModeName);
        cache[key] = info;
        return info;
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
                    row[x] = new Rgba32(p.R, p.G, p.B, 255);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] ScaleTextureAlpha(byte[] pngBytes, float scale)
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
                    var alpha = Math.Clamp((int)MathF.Round(p.A * scale), 0, 255);
                    row[x] = new Rgba32(p.R, p.G, p.B, (byte)alpha);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static bool IsAllTransparentWithUsefulRgb(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var totalPixels = image.Width * image.Height;
        if (totalPixels == 0)
            return false;

        long usefulRgbPixels = 0;
        long maxChannelSum = 0;
        byte maxAlpha = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    maxAlpha = Math.Max(maxAlpha, p.A);
                    var maxChannel = Math.Max(p.R, Math.Max(p.G, p.B));
                    maxChannelSum += maxChannel;
                    if (maxChannel >= 24)
                        usefulRgbPixels++;
                }
            }
        });

        if (maxAlpha > 8)
            return false;

        var averageMaxChannel = maxChannelSum / (double)totalPixels;
        return usefulRgbPixels * 10 >= totalPixels && averageMaxChannel >= 24.0;
    }

    private static bool TryComputeDestinationAlphaUvTransform(
        Ps2GeomLeaf sourceLeaf,
        Ps2GeomLeaf maskLeaf,
        out UvAffineTransform transform)
    {
        transform = default;
        var pairs = BuildPositionMatchedUvPairs(sourceLeaf, maskLeaf);
        if (pairs.Count < 3)
            return false;

        if (!TrySolveUvAffine(pairs, out transform))
            return false;

        var maxResidual = 0.0;
        foreach (var pair in pairs)
        {
            var (maskU, maskV) = transform.Transform(pair.SourceU, pair.SourceV);
            var du = maskU - pair.MaskU;
            var dv = maskV - pair.MaskV;
            maxResidual = Math.Max(maxResidual, Math.Sqrt(du * du + dv * dv));
        }

        return maxResidual <= 0.05;
    }

    private static List<UvPair> BuildPositionMatchedUvPairs(Ps2GeomLeaf sourceLeaf, Ps2GeomLeaf maskLeaf)
    {
        var sourceVertices = sourceLeaf.Vertices
            .Where(static v => v.HasUV)
            .ToArray();
        var maskVertices = maskLeaf.Vertices
            .Where(static v => v.HasUV)
            .ToArray();

        if (sourceVertices.Length < 3 || maskVertices.Length < 3)
            return [];

        var tolerance = ComputePositionMatchTolerance(sourceLeaf, maskLeaf);
        var toleranceSq = tolerance * tolerance;
        if (sourceVertices.Length == maskVertices.Length)
        {
            var orderedPairs = new List<UvPair>(sourceVertices.Length);
            var orderedMatch = true;
            for (var i = 0; i < sourceVertices.Length; i++)
            {
                if (Vector3.DistanceSquared(sourceVertices[i].Position, maskVertices[i].Position) > toleranceSq)
                {
                    orderedMatch = false;
                    break;
                }

                orderedPairs.Add(new UvPair(
                    sourceVertices[i].U,
                    sourceVertices[i].V,
                    maskVertices[i].U,
                    maskVertices[i].V));
            }

            if (orderedMatch)
                return orderedPairs;
        }

        var pairs = new List<UvPair>(Math.Min(sourceVertices.Length, maskVertices.Length));
        var usedMaskVertices = new bool[maskVertices.Length];
        foreach (var source in sourceVertices)
        {
            var bestIndex = -1;
            var bestDistanceSq = toleranceSq;
            for (var i = 0; i < maskVertices.Length; i++)
            {
                if (usedMaskVertices[i])
                    continue;

                var distanceSq = Vector3.DistanceSquared(source.Position, maskVertices[i].Position);
                if (distanceSq > bestDistanceSq)
                    continue;

                bestDistanceSq = distanceSq;
                bestIndex = i;
            }

            if (bestIndex < 0)
                continue;

            usedMaskVertices[bestIndex] = true;
            var mask = maskVertices[bestIndex];
            pairs.Add(new UvPair(source.U, source.V, mask.U, mask.V));
        }

        return pairs;
    }

    private static float ComputePositionMatchTolerance(Ps2GeomLeaf sourceLeaf, Ps2GeomLeaf maskLeaf)
    {
        var sourceBounds = ComputeBbox(sourceLeaf.Vertices);
        var maskBounds = ComputeBbox(maskLeaf.Vertices);
        var sourceSize = sourceBounds.Max - sourceBounds.Min;
        var maskSize = maskBounds.Max - maskBounds.Min;
        var maxDimension = Math.Max(
            Math.Max(Math.Abs(sourceSize.X), Math.Abs(sourceSize.Y)),
            Math.Max(Math.Abs(sourceSize.Z),
                Math.Max(Math.Abs(maskSize.X), Math.Max(Math.Abs(maskSize.Y), Math.Abs(maskSize.Z)))));
        return Math.Max(0.01f, maxDimension * 0.001f);
    }

    private static bool TrySolveUvAffine(IReadOnlyList<UvPair> pairs, out UvAffineTransform transform)
    {
        transform = default;
        Span<double> normal = stackalloc double[9];
        Span<double> rhsU = stackalloc double[3];
        Span<double> rhsV = stackalloc double[3];

        foreach (var pair in pairs)
        {
            var x0 = (double)pair.SourceU;
            var x1 = (double)pair.SourceV;
            var yU = (double)pair.MaskU;
            var yV = (double)pair.MaskV;

            normal[0] += x0 * x0;
            normal[1] += x0 * x1;
            normal[2] += x0;
            normal[3] += x1 * x0;
            normal[4] += x1 * x1;
            normal[5] += x1;
            normal[6] += x0;
            normal[7] += x1;
            normal[8] += 1.0;

            rhsU[0] += x0 * yU;
            rhsU[1] += x1 * yU;
            rhsU[2] += yU;
            rhsV[0] += x0 * yV;
            rhsV[1] += x1 * yV;
            rhsV[2] += yV;
        }

        Span<double> u = stackalloc double[3];
        Span<double> v = stackalloc double[3];
        if (!TrySolve3x3(normal, rhsU, u) || !TrySolve3x3(normal, rhsV, v))
            return false;

        transform = new UvAffineTransform(
            (float)u[0],
            (float)u[1],
            (float)u[2],
            (float)v[0],
            (float)v[1],
            (float)v[2]);
        return true;
    }

    private static bool TrySolve3x3(ReadOnlySpan<double> matrix, ReadOnlySpan<double> rhs, Span<double> solution)
    {
        var a00 = matrix[0];
        var a01 = matrix[1];
        var a02 = matrix[2];
        var a10 = matrix[3];
        var a11 = matrix[4];
        var a12 = matrix[5];
        var a20 = matrix[6];
        var a21 = matrix[7];
        var a22 = matrix[8];

        var det =
            a00 * (a11 * a22 - a12 * a21)
            - a01 * (a10 * a22 - a12 * a20)
            + a02 * (a10 * a21 - a11 * a20);
        if (Math.Abs(det) < 1e-8)
            return false;

        var b0 = rhs[0];
        var b1 = rhs[1];
        var b2 = rhs[2];

        solution[0] =
            (b0 * (a11 * a22 - a12 * a21)
             - a01 * (b1 * a22 - a12 * b2)
             + a02 * (b1 * a21 - a11 * b2)) / det;
        solution[1] =
            (a00 * (b1 * a22 - a12 * b2)
             - b0 * (a10 * a22 - a12 * a20)
             + a02 * (a10 * b2 - b1 * a20)) / det;
        solution[2] =
            (a00 * (a11 * b2 - b1 * a21)
             - a01 * (a10 * b2 - b1 * a20)
             + b0 * (a10 * a21 - a11 * a20)) / det;
        return true;
    }

    private static byte[] ApplyDestinationAlphaMask(
        byte[] sourcePng,
        byte[] maskPng,
        UvAffineTransform? maskFromSourceUv = null,
        ulong maskClamp1 = 0,
        ulong sourceTest1 = 0)
    {
        using var source = Image.Load<Rgba32>(sourcePng);
        using var mask = Image.Load<Rgba32>(maskPng);
        var maskAlpha = new byte[mask.Width * mask.Height];
        var clampS = (maskClamp1 & 0x03) != 0;
        var clampT = ((maskClamp1 >> 2) & 0x03) != 0;

        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    maskAlpha[y * mask.Width + x] = row[x].A;
            }
        });

        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    byte maskA;
                    if (maskFromSourceUv is { } transform)
                    {
                        var sourceU = (x + 0.5f) / row.Length;
                        var sourceV = (y + 0.5f) / accessor.Height;
                        var (maskU, maskV) = transform.Transform(sourceU, sourceV);
                        maskA = SampleAlpha(maskAlpha, mask.Width, mask.Height, maskU, maskV, clampS, clampT);
                    }
                    else
                    {
                        var maskX = x * mask.Width / row.Length;
                        var maskY = y * mask.Height / accessor.Height;
                        maskA = maskAlpha[maskY * mask.Width + maskX];
                    }

                    var sourceCoverage = ComputeSourceAlphaTestCoverage(p.A, sourceTest1);
                    var alpha = sourceCoverage * maskA / 255;
                    row[x] = new Rgba32(p.R, p.G, p.B, (byte)alpha);
                }
            }
        });

        using var ms = new MemoryStream();
        source.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte ComputeSourceAlphaTestCoverage(byte sourceAlpha, ulong test)
    {
        var ateEnabled = (test & 0x1UL) != 0;
        if (!ateEnabled)
            return 255;

        var atst = (int)((test >> 1) & 0x7);
        if (atst == 1) // ATST_ALWAYS
            return 255;

        var afail = (int)((test >> 12) & 0x3);
        if (afail is not (0 or 2))
            return 255;

        var aref = (byte)((test >> 4) & 0xFF);
        return AlphaTestPasses(sourceAlpha, aref, atst) ? (byte)255 : (byte)0;
    }

    private static bool AlphaTestPasses(byte sourceAlpha, byte aref, int atst)
    {
        return atst switch
        {
            0 => false, // NEVER
            1 => true, // ALWAYS
            2 => sourceAlpha < aref, // LESS
            3 => sourceAlpha <= aref, // LEQUAL
            4 => sourceAlpha == aref, // EQUAL
            5 => sourceAlpha >= aref, // GEQUAL
            6 => sourceAlpha > aref, // GREATER
            7 => sourceAlpha != aref, // NOTEQUAL
            _ => true
        };
    }

    private static bool ShouldBakeVertexTint(bool isWorldZoneScene, Ps2GeomLeaf leaf, byte alphaBlend)
    {
        if (!isWorldZoneScene || leaf.IsBillboard)
            return false;
        if (alphaBlend is not (0x0A or 0x1A or 0x00))
            return false;
        if (leaf.Vertices.Length == 0)
            return false;
        byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        var hasMeaningfulTint = false;
        foreach (var v in leaf.Vertices)
        {
            if (!v.HasColor) return false;
            if (v.R < minR) minR = v.R;
            if (v.R > maxR) maxR = v.R;
            if (v.G < minG) minG = v.G;
            if (v.G > maxG) maxG = v.G;
            if (v.B < minB) minB = v.B;
            if (v.B > maxB) maxB = v.B;
            if (Math.Abs(v.R - 128) > 8 || Math.Abs(v.G - 128) > 8 || Math.Abs(v.B - 128) > 8)
                hasMeaningfulTint = true;
        }

        if (!hasMeaningfulTint)
            return false;
        var maxRange = Math.Max(Math.Max(maxR - minR, maxG - minG), maxB - minB);
        return maxRange <= VertexTintBakeMaxChannelRange;
    }

    /// <summary>
    ///     Compute the leaf's average vertex tint and pack it into a 32-bit value
    ///     keyed as <c>0xFF_RR_GG_BB</c> — the high byte 0xFF is the "bake active"
    ///     sentinel that distinguishes this from the default 0 (no bake). Average
    ///     is plain arithmetic mean across all coloured vertices.
    /// </summary>
    private static uint ComputeBakedVertexTint(Ps2Vertex[] vertices)
    {
        long sumR = 0, sumG = 0, sumB = 0;
        var n = 0;
        foreach (var v in vertices)
        {
            if (!v.HasColor) continue;
            sumR += v.R;
            sumG += v.G;
            sumB += v.B;
            n++;
        }

        if (n == 0) return 0u;
        var avgR = (byte)Math.Clamp(sumR / n, 0, 255);
        var avgG = (byte)Math.Clamp(sumG / n, 0, 255);
        var avgB = (byte)Math.Clamp(sumB / n, 0, 255);
        return 0xFF000000u | ((uint)avgR << 16) | ((uint)avgG << 8) | avgB;
    }

    /// <summary>
    ///     Multiply every RGB pixel of a texture by an 8-bit per-channel tint
    ///     using PS2-style integer math (<c>out = pixel * tint / 128</c>, clamped
    ///     to 255). Alpha is left untouched. Used to bake per-leaf vertex colour
    ///     into the texture so per-vertex glTF modulation can be skipped.
    /// </summary>
    private static byte[] ModulateTextureBy8BitTint(byte[] pngBytes, byte tintR, byte tintG, byte tintB)
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
                    var r = (byte)Math.Min(255, p.R * tintR / 128);
                    var g = (byte)Math.Min(255, p.G * tintG / 128);
                    var b = (byte)Math.Min(255, p.B * tintB / 128);
                    row[x] = new Rgba32(r, g, b, p.A);
                }
            }
        });
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static bool IsStandardSourceAlphaBlend(byte alphaBlend)
    {
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        return aField == 0 && bField == 1 && cField == 0 && dField == 1;
    }

    private static bool UsesDestinationAlphaBlend(byte alphaBlend)
    {
        var cField = (alphaBlend >> 4) & 0x03;
        return cField == 1;
    }

    /// <summary>
    ///     Synthesis is eligible (per upstream check) for both <c>synthesize</c>
    ///     and <c>blend</c> strategies. In both we attempt to bake the prior
    ///     same-bbox sibling's alpha into the C=Ad consumer texture; the
    ///     difference is the fallback when no exact sibling exists — see
    ///     <see cref="DestAlphaOverrideForCField" />.
    /// </summary>
    private static bool DestAlphaSynthesisEligible()
    {
        return ReadDestAlphaStrategy() is "synthesize" or "blend";
    }

    private static DestAlphaOverride? DestAlphaOverrideForCField(int cField)
    {
        if (cField != 1)
            return null; // not a C=Ad material — no override
        return ReadDestAlphaStrategy() switch
        {
            "opaque" => DestAlphaOverride.Opaque,
            // "blend" only forces BLEND when synthesis upstream produced no
            // mask — i.e. there's no exact-bbox earlier sibling. The override
            // is gated by texChecksum still equalling the original source
            // (not a synthetic checksum), checked in GetOrCreateGeomMaterial.
            "blend" => DestAlphaOverride.Blend,
            _ => null // synthesize / unknown — leave to existing logic
        };
    }

    private static string ReadDestAlphaStrategy()
    {
        var v = Environment.GetEnvironmentVariable("THAW_DEST_ALPHA");
        if (string.IsNullOrWhiteSpace(v)) return "synthesize";
        return v.Trim().ToLowerInvariant();
    }

    private static uint CreateSyntheticTextureChecksum(uint sourceChecksum, uint maskChecksum)
    {
        var hash = 0xA1F3D5B7u;
        hash ^= RotateLeft(sourceChecksum, 7);
        hash ^= RotateLeft(maskChecksum, 19);
        return hash | 0x80000000u;
    }

    private static uint RotateLeft(uint value, int shift)
    {
        return (value << shift) | (value >> (32 - shift));
    }


    private static byte[] ResolveDitheredAlpha(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var width = image.Width;
        var height = image.Height;
        var original = new Rgba32[width * height];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).CopyTo(original.AsSpan(y * width, width));
        });

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var alphaSum = 0;
                    var sampleCount = 0;
                    var alphaWeight = 0;
                    var rSum = 0;
                    var gSum = 0;
                    var bSum = 0;

                    for (var yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                    {
                        for (var xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                        {
                            var p = original[yy * width + xx];
                            alphaSum += p.A;
                            sampleCount++;
                            if (p.A == 0)
                                continue;

                            alphaWeight += p.A;
                            rSum += p.R * p.A;
                            gSum += p.G * p.A;
                            bSum += p.B * p.A;
                        }
                    }

                    var current = original[y * width + x];
                    var alpha = (byte)(alphaSum / Math.Max(1, sampleCount));
                    if (alphaWeight == 0)
                    {
                        row[x] = new Rgba32(current.R, current.G, current.B, alpha);
                    }
                    else
                    {
                        row[x] = new Rgba32(
                            (byte)(rSum / alphaWeight),
                            (byte)(gSum / alphaWeight),
                            (byte)(bSum / alphaWeight),
                            alpha);
                    }
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte SampleAlpha(
        byte[] alpha,
        int width,
        int height,
        float u,
        float v,
        bool clampS,
        bool clampT)
    {
        var x = TextureCoordinateToPixel(u, width, clampS);
        var y = TextureCoordinateToPixel(v, height, clampT);
        return alpha[y * width + x];
    }

    private static int TextureCoordinateToPixel(float coordinate, int length, bool clamp)
    {
        if (length <= 1)
            return 0;

        double normalized;
        if (clamp)
        {
            normalized = Math.Clamp(coordinate, 0f, 1f);
            var clamped = (int)Math.Floor(normalized * length);
            return Math.Min(length - 1, Math.Max(0, clamped));
        }

        normalized = coordinate - Math.Floor(coordinate);
        if (normalized < 0)
            normalized += 1.0;
        var repeated = (int)Math.Floor(normalized * length);
        return Math.Min(length - 1, Math.Max(0, repeated));
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

        // Low-opacity masks and glass overlays often have no fully opaque pixels
        // after the PS2 texture decoder has scaled them into normal PNG alpha.
        // They are real blended surfaces, not hard cutouts; exporting them as MASK
        // either drops them completely or turns them into solid cards.
        if (high == 0 && mid > 0)
            return AlphaProfile.Graduated;

        // Extremes (a=0 + a=255) ≥ 80% of pixels: most of the image is opaque or
        // fully transparent, the rest is antialiasing noise. MASK at 0.5 keeps the
        // hard outline and writes to depth so geometry behind correctly occludes.
        // The 80% threshold is empirical: palm-leaf cutouts come in around 85-95%
        // extremes (5-15% AA fringe); soft shadow textures come in well below 50%.
        if ((low + high) * 5 >= total * 4)
            return AlphaProfile.Bimodal;

        return AlphaProfile.Graduated;
    }

    private static bool HasUsefulDestinationAlpha(byte[] pngBytes)
    {
        return AnalyzeAlphaProfile(pngBytes) != AlphaProfile.AllOpaque;
    }

    /// <summary>
    ///     Whether the mask should be flattened to its average alpha before
    ///     synthesis. True for "decorative pattern" masks — small alpha
    ///     textures (containing transparent pixels) that tile many times
    ///     across a large consumer surface, where keeping the per-texel
    ///     pattern would produce a visible tile grid in the synthesized
    ///     output. The PS2 GS doesn't show tiles in this case because of
    ///     mipmap minification + bilinear filtering at high LOD: the
    ///     per-texel pattern blurs to a near-uniform tint.
    /// </summary>
    /// <remarks>
    ///     Single-instance shape masks (≤4× tiling) skip the flattening so
    ///     dirt-patch silhouettes and other purposeful cutouts retain their
    ///     local detail. Masks without any transparent pixels (e.g. grass-
    ///     base alpha-grade textures) also skip — they already render as
    ///     uniform opaque whether tiled or not.
    /// </remarks>
    private static bool MaskShouldFlattenToAverage(byte[] maskPng, Ps2GeomLeaf maskLeaf)
    {
        const float maxTilingFactor = 4.0f;

        using var image = Image.Load<Rgba32>(maskPng);
        var texW = image.Width;
        var texH = image.Height;
        if (texW <= 0 || texH <= 0)
            return false;

        var hasTransparentPixels = false;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !hasTransparentPixels; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A == 0)
                    {
                        hasTransparentPixels = true;
                        break;
                    }
                }
            }
        });
        if (!hasTransparentPixels)
            return false;

        var (min, max) = ComputeBbox(maskLeaf.Vertices);
        var size = max - min;
        var sx = MathF.Abs(size.X);
        var sy = MathF.Abs(size.Y);
        var sz = MathF.Abs(size.Z);
        var maxAxis = MathF.Max(sx, MathF.Max(sy, sz));
        var minAxis = MathF.Min(sx, MathF.Min(sy, sz));
        var midAxis = sx + sy + sz - maxAxis - minAxis;
        if (maxAxis <= 0f || midAxis <= 0f)
            return false;

        var tilingMajor = maxAxis / texW;
        var tilingMinor = midAxis / texH;
        if (tilingMajor < tilingMinor) (tilingMajor, tilingMinor) = (tilingMinor, tilingMajor);

        return tilingMajor > maxTilingFactor;
    }

    /// <summary>
    ///     Returns a 1×1 fully-opaque PNG. Used as the synthesis mask when the
    ///     paired earlier sibling was an opaque draw (alpha1 in {0x0A,0x1A,0x00}):
    ///     the GS wrote framebuffer alpha=1 uniformly, so the C=Ad consumer
    ///     should render with no masking — its texture stays as-is.
    /// </summary>
    private static byte[] CreateUniformOpaqueMask()
    {
        using var flat = new Image<Rgba32>(1, 1, new Rgba32(255, 255, 255, 255));
        using var ms = new MemoryStream();
        flat.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Returns a 1×1 PNG with the source mask's average alpha and a
    ///     placeholder RGB. <see cref="ApplyDestinationAlphaMask" /> samples
    ///     mask alpha at the source UVs, so a 1×1 mask reads the same alpha
    ///     for every source texel — a uniform translucent overlay.
    /// </summary>
    private static byte[] FlattenMaskAlphaToAverage(byte[] maskPng)
    {
        using var src = Image.Load<Rgba32>(maskPng);
        long sum = 0;
        long count = 0;
        src.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    sum += row[x].A;
                    count++;
                }
            }
        });
        var avgAlpha = count > 0 ? (byte)(sum / count) : (byte)0;

        using var flat = new Image<Rgba32>(1, 1, new Rgba32(255, 255, 255, avgAlpha));
        using var ms = new MemoryStream();
        flat.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Detect a high-frequency dithered alpha pattern: pixels at extreme alpha
    ///     (a=0 or a=255) that alternate every 1-2 pixels. Returns true when the
    ///     fraction of horizontal-and-vertical neighbour pairs that flip between
    ///     the two extremes exceeds a small threshold.
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

    private static bool IsDarkAlphaOverlay(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        long visiblePixels = 0;
        long weightedMaxChannel = 0;
        long alphaWeight = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A <= 8)
                        continue;

                    visiblePixels++;
                    weightedMaxChannel += Math.Max(p.R, Math.Max(p.G, p.B)) * p.A;
                    alphaWeight += p.A;
                }
            }
        });

        if (visiblePixels == 0 || alphaWeight == 0)
            return false;

        var totalPixels = image.Width * image.Height;
        if (visiblePixels * 100 < totalPixels)
            return false;

        var averageMaxChannel = weightedMaxChannel / (double)alphaWeight;
        return averageMaxChannel <= 32.0;
    }

    /// <summary>
    ///     Detects "monochrome alpha-mask" textures: stencils where every visible
    ///     pixel shares a single RGB color (any color — pure white, pure black,
    ///     a flat tint) and the actual shape lives entirely in the alpha channel.
    ///     The classic case is shadow cards: white texture × dark vertex colours
    ///     × graduated alpha = soft-edged shadow on the receiver. Forcing these
    ///     into MASK alpha mode at the bimodal threshold clips the alpha gradient
    ///     and turns the soft shadow into a hard cutout — visible as fully opaque
    ///     edges instead of feathered ones.
    /// </summary>
    /// <remarks>
    ///     Heuristic: the visible pixels' RGB has near-zero channel range
    ///     (max - min ≤ 8 per pixel, on average), <em>and</em> non-trivial
    ///     mid-alpha pixels exist (≥ 2% of visible pixels carry 8 ≤ alpha &lt; 248
    ///     — actual gradient, not just a binary cutout). The 6CBB6DE0 cannon
    ///     shadow in z_sm hits this: RGB ≈ (253, 253, 253) for every visible
    ///     pixel, with a soft alpha falloff at the silhouette edges.
    /// </remarks>
    private static bool IsMonochromeAlphaMask(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        long visiblePixels = 0;
        long midAlphaPixels = 0;
        long channelRangeWeight = 0;
        long alphaWeight = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A <= 8)
                        continue;
                    visiblePixels++;
                    alphaWeight += p.A;
                    var maxChannel = Math.Max(p.R, Math.Max(p.G, p.B));
                    var minChannel = Math.Min(p.R, Math.Min(p.G, p.B));
                    channelRangeWeight += (maxChannel - minChannel) * p.A;
                    if (p.A is >= 8 and < 248)
                        midAlphaPixels++;
                }
            }
        });

        if (visiblePixels == 0 || alphaWeight == 0)
            return false;

        var averageChannelRange = channelRangeWeight / (double)alphaWeight;
        // Tight monochrome threshold: avg per-pixel channel-range ≤ 8 means
        // every visible pixel is within 8/255 of a single grey level.
        if (averageChannelRange > 8.0)
            return false;

        // Need real gradient pixels — not just a binary 0/255 cutout. The 2%
        // floor lets z_bh foliage cards (which can be technically monochrome
        // too but use a hard cutout) stay on the MASK path.
        return midAlphaPixels * 50 >= visiblePixels;
    }

    private static bool IsLikelySoftShadowOverlay(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var totalPixels = image.Width * image.Height;
        if (totalPixels == 0)
            return false;

        long visiblePixels = 0;
        long highAlphaPixels = 0;
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
                        continue;

                    var maxChannel = Math.Max(p.R, Math.Max(p.G, p.B));
                    var minChannel = Math.Min(p.R, Math.Min(p.G, p.B));
                    visiblePixels++;
                    if (p.A >= 248)
                        highAlphaPixels++;
                    alphaWeight += p.A;
                    maxChannelWeight += maxChannel * p.A;
                    channelRangeWeight += (maxChannel - minChannel) * p.A;
                }
            }
        });

        if (visiblePixels == 0 || alphaWeight == 0)
            return false;

        // These are the broad, grey/black texture-card shadows. They cover much of
        // the card, have a meaningful opaque-alpha component, and are low-saturation.
        // Foliage, boardwalk masks, and water either have sparse coverage, colourful
        // pixels, or only mid-alpha pixels and should stay on their normal path.
        if (visiblePixels * 10 < totalPixels * 7)
            return false;
        if (highAlphaPixels * 5 < visiblePixels)
            return false;

        var averageMaxChannel = maxChannelWeight / (double)alphaWeight;
        var averageChannelRange = channelRangeWeight / (double)alphaWeight;
        return averageMaxChannel <= 128.0 && averageChannelRange <= 32.0;
    }

    private static bool IsLikelyFoliageCutout(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var totalPixels = image.Width * image.Height;
        if (totalPixels == 0)
            return false;

        long lowAlphaPixels = 0;
        long visiblePixels = 0;
        long alphaWeight = 0;
        long redWeight = 0;
        long greenWeight = 0;
        long blueWeight = 0;

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

                    visiblePixels++;
                    alphaWeight += p.A;
                    redWeight += p.R * p.A;
                    greenWeight += p.G * p.A;
                    blueWeight += p.B * p.A;
                }
            }
        });

        if (visiblePixels == 0 || alphaWeight == 0)
            return false;

        // Foliage cards commonly have large transparent regions and many antialias
        // alpha levels, so the generic alpha histogram treats them as BLEND. Use the
        // visible colour bias to keep these hard-depth cutouts as MASK instead.
        if (lowAlphaPixels * 20 < totalPixels)
            return false;

        var averageRed = redWeight / (double)alphaWeight;
        var averageGreen = greenWeight / (double)alphaWeight;
        var averageBlue = blueWeight / (double)alphaWeight;
        return averageGreen >= averageRed * 1.03
               && averageGreen >= averageBlue * 1.05
               && averageGreen >= 50.0
               && averageRed <= 170.0;
    }

    private static bool IsExtremeAlphaFlip(byte a1, byte a2)
    {
        return (a1 == 0 && a2 == 255) || (a1 == 255 && a2 == 0);
    }

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
