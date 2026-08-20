using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     THAW PS2 worldzone leaves: per-sector placement, leaf filtering, and
///     billboard/overlay vertex transforms.
/// </summary>
internal static class Ps2WorldzoneGeometryWriter
{
    public static void PopulatePs2Worldzone(
        ModelDocument document,
        byte[] pakBytes,
        string sourceName,
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        ZoneTextureCatalog? textureCatalog,
        string? textureSourceHint,
        WorldzoneTimeOfDay timeOfDay,
        float coordinateScale,
        Ps2WorldzoneLighting? lighting = null,
        Ps2GeomDebugCollector? debugCollector = null,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Worldzone coordinate scale must be a finite positive value.");

        // THAW worldzone MDLs normally do not expose a trusted normal stream.
        // Leave vertex colours as parsed unless a caller explicitly opts into
        // the synthetic worldzone lighting model. Guard the entire conversion,
        // including archive discovery and early returns, because this is
        // ThreadStatic state.
        ModelDocumentGeometryAdapter.ActivePs2WorldzoneLighting = lighting;
        try
        {
            var typedEntries = PakArchive.GetTypedEntries(pakBytes);
            var isNetworkArchive =
                Path.GetFileName(sourceName).Contains("_net.", StringComparison.OrdinalIgnoreCase);
            var qbObjectResources = Ps2WorldzoneQbObjectResolver.Resolve(
                pakBytes,
                typedEntries,
                isNetworkArchive);
            var nodeGates = Ps2WorldzoneQbObjectResolver.ResolveLevelGeometryGates(pakBytes, typedEntries);
            var enabledVariants = RegisterVariantVisibilityGroups(document, nodeGates, visibilityOverrides);

            Ps2WorldzoneNodeGates? GateFor(Ps2GeomLeaf leaf) =>
                leaf.Checksum != 0 && nodeGates.TryGetValue(leaf.Checksum, out var gate) ? gate : null;

            // The engine's load-state rule (cfuncs.cpp:6276-6281): a sector
            // without CreatedAtStart is inactive until a script activates it.
            // TOD groups map onto the Day/Night export selection; story-state
            // (createdfromvariable) sectors export behind default-off
            // visibility groups; net-absent sectors drop from _net archives.
            string? ExcludeReason(Ps2GeomLeaf leaf)
            {
                var gate = GateFor(leaf);
                if (gate != null)
                {
                    if (isNetworkArchive && gate.AbsentInNetGames)
                        return "absent_in_net_games";
                    if (gate.CreatedFromVariable != 0 && !enabledVariants.Contains(gate.CreatedFromVariable))
                        return "variant_state_disabled";
                }

                return ShouldIncludeWorldzoneLeaf(leaf, timeOfDay, gate)
                    ? null
                    : "time_of_day_night_overlay";
            }
            var mdlEntries = typedEntries
                .Where(static entry => entry.TypeHash is
                    Ps2WorldzoneDetection.WorldzoneMdlTypeHash or
                    Ps2WorldzoneDetection.WorldzoneLevelMdlTypeHash)
                .Select(static entry => entry.Entry)
                .ToList();

            if (mdlEntries.Count == 0)
            {
                ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
                return;
            }

            document.NativeMetadata.Add(new Ps2WorldzoneRenderMetadata(
                sourceName,
                mdlEntries.Count,
                timeOfDay.ToString(),
                coordinateScale));

            var materialCache = new Dictionary<Ps2WorldzoneMaterialWriter.Ps2WorldzoneMaterialKey, int>();
            // QB-placed props are emitted AFTER every level MDL so the level
            // floor exists in the document for the spawn-seating query below.
            var qbWork = new List<(string MdlName, Ps2GeomScene Scene,
                Ps2Tex0ChecksumResolver? Tex0Resolver,
                Func<ulong, uint, Ps2GeomTextureResolution>? DebugTex0Resolver,
                List<(Vector3 Position, Quaternion Rotation)> Placements)>();
            foreach (var mdlEntry in mdlEntries)
            {
                var entryName = $"{mdlEntry.Offset:X8}";
                if (mdlEntry.Offset < 0 ||
                    mdlEntry.Size <= 0 ||
                    mdlEntry.Offset + mdlEntry.Size > pakBytes.Length)
                {
                    debugCollector?.AddRejection(new Ps2GeomLeafRejection(
                        entryName, "writer", "entry_out_of_bounds", -1, 0, 0,
                        Vector3.Zero, Vector3.Zero));
                    continue;
                }

                var mdlData = new byte[mdlEntry.Size];
                Array.Copy(pakBytes, mdlEntry.Offset, mdlData, 0, (int)mdlEntry.Size);
                var mdlName = entryName;
                mdlData = Ps2WorldzoneMdlPreamble.ExtendLevelMdlPreambleIfNeeded(pakBytes, mdlEntry, mdlData);
                if (!Ps2GeomFile.IsPakMdl(mdlData))
                {
                    debugCollector?.AddRejection(new Ps2GeomLeafRejection(
                        mdlName, "writer", "not_pak_mdl", -1, 0, 0,
                        Vector3.Zero, Vector3.Zero));
                    continue;
                }

                var mdlTextureHint = textureCatalog?.FindTextureEntryHintBefore(textureSourceHint, mdlEntry.Offset)
                                     ?? textureSourceHint;
                var mdlTex0Resolver = textureCatalog?.CreateTex0ChecksumResolver(mdlTextureHint)
                                      ?? tex0Resolver;
                var mdlDebugTex0Resolver = debugCollector != null
                    ? textureCatalog?.CreateDebugTex0Resolver(mdlTextureHint)
                    : null;
                var geomScene = Ps2GeomFile.ParsePakMdl(
                    mdlData,
                    mdlName,
                    debugCollector != null ? debugCollector.AddRejection : null);

                // Compact THAW object resources have no MDL bone-placement table.
                // Their preceding model QB establishes ownership and the main NQB
                // supplies every authored instance. A successfully resolved empty
                // set is intentional (for example script-created objects), so it
                // suppresses the legacy origin fallback too.
                if (geomScene.MdlPreamble is { Bones.Count: 0 } &&
                    qbObjectResources.TryGetValue(mdlEntry.Offset, out var qbResource))
                {
                    if (qbResource.Instances.Count > 0)
                    {
                        var qbPlacements = qbResource.Instances
                            .Select(static instance => (instance.Position, instance.Rotation))
                            .ToList();
                        qbWork.Add((mdlName, geomScene, mdlTex0Resolver, mdlDebugTex0Resolver, qbPlacements));
                    }
                    else
                    {
                        debugCollector?.AddRejection(new Ps2GeomLeafRejection(
                            mdlName, "writer", "qb_resolved_empty", -1,
                            geomScene.Leaves.Count, 0, Vector3.Zero, Vector3.Zero));
                    }

                    continue;
                }

                var placements = geomScene.MdlPreamble?.Bones.Count > 0
                    ? Ps2MdlPlacementResolver.ResolveWorldzonePlacements(geomScene.MdlPreamble)
                    : [];

                var rootPlacements = new List<(Vector3 Position, Quaternion Rotation)>(1);
                var bonePlacements = new List<(Vector3 Position, Quaternion Rotation)>();
                if (placements.Count > 0)
                {
                    rootPlacements.Add((placements[0].Position, placements[0].Rotation));
                    bonePlacements.AddRange(placements.Skip(1).Select(static p => (p.Position, p.Rotation)));
                }
                else
                {
                    rootPlacements.Add((Vector3.Zero, Quaternion.Identity));
                }

                PopulatePs2WorldzoneLeaves(
                    document,
                    geomScene,
                    mdlName,
                    rootPlacements,
                    leaf => leaf.IsLocalSpace ? "wrong_space" : ExcludeReason(leaf),
                    materialCache,
                    textureProvider,
                    texaTextureProvider,
                    mdlTex0Resolver,
                    coordinateScale,
                    "world",
                    debugCollector,
                    mdlDebugTex0Resolver,
                    GateFor);

                if (bonePlacements.Count > 0)
                {
                    PopulatePs2WorldzoneLeaves(
                        document,
                        geomScene,
                        mdlName,
                        bonePlacements,
                        leaf => leaf.IsLocalSpace ? ExcludeReason(leaf) : "wrong_space",
                        materialCache,
                        textureProvider,
                        texaTextureProvider,
                        mdlTex0Resolver,
                        coordinateScale,
                        "local",
                        debugCollector,
                        mdlDebugTex0Resolver,
                        GateFor);
                }
                else if (debugCollector != null)
                {
                    // Local-space leaves need a bone placement to stand anywhere;
                    // without one they cannot be emitted — but they must never
                    // vanish silently (the z_ch_net "missing computers" report).
                    for (var leafIndex = 0; leafIndex < geomScene.Leaves.Count; leafIndex++)
                    {
                        var leaf = geomScene.Leaves[leafIndex];
                        if (!leaf.IsLocalSpace)
                            continue;
                        var (dropMin, dropMax) = ComputeBbox(leaf.Vertices);
                        debugCollector.AddRejection(new Ps2GeomLeafRejection(
                            mdlName, "writer", "local_space_no_bone_placements", leafIndex,
                            leaf.Vertices.Length, leaf.DmaTex0, dropMin, dropMax));
                    }
                }
            }

            if (qbWork.Count > 0)
            {
                var floorTriangles = CollectWorldFloorTriangles(document);
                foreach (var work in qbWork)
                {
                    PopulatePs2WorldzoneLeaves(
                        document,
                        work.Scene,
                        work.MdlName,
                        SeatQbPlacementsOnFloor(work.Scene, work.Placements, floorTriangles),
                        ExcludeReason,
                        materialCache,
                        textureProvider,
                        texaTextureProvider,
                        work.Tex0Resolver,
                        coordinateScale,
                        "qb",
                        debugCollector,
                        work.DebugTex0Resolver,
                        GateFor);
                }
            }

            ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        }
        finally
        {
            ModelDocumentGeometryAdapter.ActivePs2WorldzoneLighting = null;
        }
    }

    /// <summary>
    ///     Rest each QB prop's bounding-box bottom on the level floor at its
    ///     own footprint. The engine seats these game objects at spawn —
    ///     PCSX2 GS captures show the z_ho patio chairs standing ON the floor
    ///     while their authored node Y sits AT floor level and the prop
    ///     models are vertically center-pivoted, which sinks a faithful
    ///     origin-at-node placement by half the model height (chairs 19-24.5
    ///     units of a 46-unit model; plants author a half-height up and
    ///     already rest). Lift-only and fail-open: no floor found, or a lift
    ///     beyond half the model height plus slack, keeps the authored Y.
    /// </summary>
    private static List<(Vector3 Position, Quaternion Rotation)> SeatQbPlacementsOnFloor(
        Ps2GeomScene scene,
        List<(Vector3 Position, Quaternion Rotation)> placements,
        IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> floorTriangles)
    {
        if (floorTriangles.Count == 0 || placements.Count == 0)
            return placements;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var leaf in scene.Leaves)
        {
            var (leafMin, leafMax) = ComputeBbox(leaf.Vertices);
            min = Vector3.Min(min, leafMin);
            max = Vector3.Max(max, leafMax);
        }

        if (min.X > max.X)
            return placements;

        Span<Vector3> corners =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z),
            new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
            new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z)
        ];
        var bottomCenter = new Vector3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);
        var maxLift = (max.Y - min.Y) * 0.5f + 10f;

        var seated = new List<(Vector3 Position, Quaternion Rotation)>(placements.Count);
        foreach (var (position, rotation) in placements)
        {
            var baseY = float.MaxValue;
            var topY = float.MinValue;
            foreach (var corner in corners)
            {
                var cornerY = Vector3.Transform(corner, rotation).Y + position.Y;
                baseY = Math.Min(baseY, cornerY);
                topY = Math.Max(topY, cornerY);
            }

            var centerY = (baseY + topY) * 0.5f;

            // Minimal-correction seating: among floors between the base and
            // the spawn height (the vertical center — these props are
            // center-pivoted, so that is the authored node height), take the
            // FIRST supporting surface at-or-above the base. Highest-below-X
            // rules climb terraced gardens; nearest-by-distance picks the
            // terrain UNDER a sloped road. The floor is sampled at the
            // bottom centre AND the four bottom corners (a rotated prop's
            // centre can hang over a curb gap while its feet stand on the
            // road). A prop already resting (or floating) is left alone.
            Span<Vector3> samples =
            [
                Vector3.Transform(bottomCenter, rotation) + position,
                Vector3.Transform(corners[0], rotation) + position,
                Vector3.Transform(corners[1], rotation) + position,
                Vector3.Transform(corners[2], rotation) + position,
                Vector3.Transform(corners[3], rotation) + position
            ];
            float? bestLift = null;
            foreach (var (a, b, c) in floorTriangles)
            {
                foreach (var sample in samples)
                {
                    if (!TryGetTrianglePlaneY(a, b, c, sample.X, sample.Z, out var y))
                        continue;
                    if (y > centerY + 1f || y < baseY - 1f)
                        continue;
                    var candidate = y - baseY;
                    if (bestLift == null || candidate < bestLift.Value)
                        bestLift = candidate;
                }
            }

            seated.Add(bestLift is { } lift && lift > 0f && lift <= maxLift
                ? (position with { Y = position.Y + lift }, rotation)
                : (position, rotation));
        }

        return seated;
    }

    private static List<(Vector3 A, Vector3 B, Vector3 C)> CollectWorldFloorTriangles(
        ModelDocument document)
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        foreach (var node in document.Nodes)
        {
            if (node.MeshIndex is not { } meshIndex)
                continue;
            var mesh = document.Meshes[meshIndex];
            if (!mesh.Name.Contains("_world_leaf_", StringComparison.Ordinal))
                continue;
            foreach (var primitive in mesh.Primitives)
            {
                for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
                {
                    triangles.Add((
                        Vector3.Transform(primitive.Vertices[primitive.Indices[i]].Position, node.Transform),
                        Vector3.Transform(primitive.Vertices[primitive.Indices[i + 1]].Position, node.Transform),
                        Vector3.Transform(primitive.Vertices[primitive.Indices[i + 2]].Position, node.Transform)));
                }
            }
        }

        return triangles;
    }

    private static bool TryGetTrianglePlaneY(
        Vector3 a, Vector3 b, Vector3 c, float x, float z, out float y)
    {
        y = 0f;
        var v0 = new Vector2(c.X - a.X, c.Z - a.Z);
        var v1 = new Vector2(b.X - a.X, b.Z - a.Z);
        var v2 = new Vector2(x - a.X, z - a.Z);

        var dot00 = Vector2.Dot(v0, v0);
        var dot01 = Vector2.Dot(v0, v1);
        var dot02 = Vector2.Dot(v0, v2);
        var dot11 = Vector2.Dot(v1, v1);
        var dot12 = Vector2.Dot(v1, v2);

        var denom = dot00 * dot11 - dot01 * dot01;
        if (Math.Abs(denom) < 1e-6f)
            return false;

        var inv = 1f / denom;
        var u = (dot11 * dot02 - dot01 * dot12) * inv;
        var v = (dot00 * dot12 - dot01 * dot02) * inv;
        if (u < -1e-4f || v < -1e-4f || u + v > 1f + 1e-4f)
            return false;

        y = a.Y + u * (c.Y - a.Y) + v * (b.Y - a.Y);
        return true;
    }

    internal static void PopulatePs2WorldzoneLeaves(
        ModelDocument document,
        Ps2GeomScene scene,
        string mdlName,
        List<(Vector3 Position, Quaternion Rotation)> placements,
        Func<Ps2GeomLeaf, string?> leafExclusion,
        Dictionary<Ps2WorldzoneMaterialWriter.Ps2WorldzoneMaterialKey, int> materialCache,
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        float coordinateScale,
        string space,
        Ps2GeomDebugCollector? debugCollector = null,
        Func<ulong, uint, Ps2GeomTextureResolution>? debugTex0Resolver = null,
        Func<Ps2GeomLeaf, Ps2WorldzoneNodeGates?>? gateResolver = null)
    {
        var instances = placements.Count > 0
            ? placements
            : [(Vector3.Zero, Quaternion.Identity)];
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw(scene.Leaves);
        var sourceTextureProvider =
            Ps2WorldzoneMaterialWriter.ResolvePs2TexaAwareProvider(textureProvider, texaTextureProvider);
        var syntheticTextures = new Dictionary<uint, byte[]>();
        Ps2TexaTextureResolver? effectiveTexaTextureProvider = sourceTextureProvider == null
            ? null
            : (checksum, texa) => syntheticTextures.TryGetValue(checksum, out var syntheticPng)
                ? syntheticPng
                : sourceTextureProvider(checksum, texa);
        var destinationAlphaMasks = Ps2GeomDestinationAlphaSynthesis.BuildMaskCandidates(
            orderedLeaves,
            sourceTextureProvider,
            tex0Resolver,
            leaf => leafExclusion(leaf) == null,
            ShouldSkipWorldzoneLeaf);
        var recentAlphaMasks = new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>();
        var overlapGroupIds = new Dictionary<Ps2DestinationAlphaLeafGeometryKey, int>();
        var overlapPassCounters = new Dictionary<Ps2DestinationAlphaLeafGeometryKey, int>();

        // Rejections are logged only for leaves this pass OWNS by space — the
        // world/local passes each see every leaf and filter the other space
        // out, so logging unconditionally would record every leaf as
        // "filtered" once per pass.
        Func<Ps2GeomLeaf, bool> ownsLeaf = space switch
        {
            "world" => static leaf => !leaf.IsLocalSpace,
            "local" => static leaf => leaf.IsLocalSpace,
            _ => static _ => true
        };

        foreach (var drawItem in orderedLeaves)
        {
            var leaf = drawItem.Leaf;
            var leafIndex = drawItem.LeafIndex;
            var leafGates = gateResolver?.Invoke(leaf);
            var exclusionReason = leafExclusion(leaf);
            if (leaf.Vertices.Length < 3 ||
                exclusionReason != null ||
                ShouldSkipWorldzoneLeaf(leaf))
            {
                if (debugCollector != null && ownsLeaf(leaf))
                {
                    var (skipMin, skipMax) = ComputeBbox(leaf.Vertices);
                    var reason = leaf.Vertices.Length < 3 ? "too_few_vertices"
                        : exclusionReason ?? "geometric_quarantine";
                    debugCollector.AddRejection(new Ps2GeomLeafRejection(
                        mdlName, "writer", reason, leafIndex,
                        leaf.Vertices.Length, leaf.DmaTex0, skipMin, skipMax));
                }

                continue;
            }

            var textureChecksum = Ps2WorldzoneMaterialWriter.ResolvePs2GeomTextureChecksum(leaf, tex0Resolver);
            var geometryKey = Ps2GeomDestinationAlphaSynthesis.CreateLeafGeometryKey(leaf);
            if (Ps2WorldzoneMaterialWriter.ShouldSkipRedundantWorldzoneBlendLayer(leaf, textureChecksum, geometryKey,
                    recentAlphaMasks))
            {
                if (debugCollector != null && ownsLeaf(leaf))
                {
                    var (skipMin, skipMax) = ComputeBbox(leaf.Vertices);
                    debugCollector.AddRejection(new Ps2GeomLeafRejection(
                        mdlName, "writer", "redundant_blend_layer", leafIndex,
                        leaf.Vertices.Length, leaf.DmaTex0, skipMin, skipMax));
                }

                continue;
            }

            var usesSynthesizedDestinationAlpha = false;
            if (textureChecksum != 0 && effectiveTexaTextureProvider != null &&
                Ps2GeomDestinationAlphaSynthesis.TryCreateSyntheticTexture(
                    leaf,
                    textureChecksum,
                    Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(leaf),
                    destinationAlphaMasks,
                    recentAlphaMasks,
                    effectiveTexaTextureProvider,
                    syntheticTextures,
                    out var syntheticTextureChecksum))
            {
                textureChecksum = syntheticTextureChecksum;
                usesSynthesizedDestinationAlpha = true;
            }

            var alphaModePng = textureChecksum != 0
                ? effectiveTexaTextureProvider?.Invoke(textureChecksum, leaf.DmaTexa)
                : null;
            var alphaMode =
                Ps2MaterialWriter.ClassifyPs2GeomEffectiveAlphaMode(leaf, alphaModePng,
                    usesSynthesizedDestinationAlpha);
            // Vertices are exported at their AUTHORED positions. The PS2 resolves
            // coplanar multi-pass stacks by submission order, so instead of baking
            // a depth-bias offset into the mesh (the pre-B1 approach — it corrupted
            // every blend/mask leaf's geometry and its sub-millimetre magnitude was
            // below depth-buffer precision at distance anyway), draw order ships as
            // metadata: DrawIndex/PassIndex reach GLB extras (the three.js viewer
            // enforces submission order via renderOrder + LEQUAL, exactly like the
            // GS) and the .blend importer applies the separation vector below at
            // OBJECT level, where EEVEE needs it and users can remove it.
            if (!overlapGroupIds.TryGetValue(geometryKey, out var overlapGroup))
            {
                overlapGroup = overlapGroupIds.Count;
                overlapGroupIds[geometryKey] = overlapGroup;
            }

            overlapPassCounters.TryGetValue(geometryKey, out var passIndex);
            overlapPassCounters[geometryKey] = passIndex + 1;

            var depthBias = Ps2GeomRenderSemantics.ComputeWorldzoneMaterialDepthBias(leaf, alphaMode, passIndex);
            var blendOffset = Vector3.Zero;
            if (depthBias > 0f && coordinateScale > 0f)
            {
                var offsetDirection = ComputeOverlayOffsetDirection(leaf.Vertices);
                blendOffset = offsetDirection * (depthBias * coordinateScale);
            }

            var sourceVertices = leaf.Vertices;
            var (min, max) = ComputeBbox(sourceVertices);
            var localOrigin = (min + max) * 0.5f;
            var localizedVertices = LocalizePs2Vertices(sourceVertices, localOrigin, coordinateScale);

            var materialIndex = Ps2WorldzoneMaterialWriter.GetOrCreatePs2WorldzoneMaterial(
                document,
                materialCache,
                leaf,
                null,
                effectiveTexaTextureProvider,
                tex0Resolver,
                textureChecksum,
                usesSynthesizedDestinationAlpha,
                alphaMode);
            var preserveVertexAlpha = Ps2SceneGeometryWriter.ShouldPreservePs2GeomVertexAlpha(leaf, alphaMode);

            var emittedLeaf = false;
            for (var placementIndex = 0; placementIndex < instances.Count; placementIndex++)
            {
                var (position, rotation) = instances[placementIndex];
                var mesh = new ModelMesh
                {
                    Name = $"{mdlName}_{space}_leaf_{leafIndex:D5}"
                };
                var primitive = Ps2SceneGeometryWriter.AddPs2StripPrimitive(
                    mesh,
                    "strip",
                    materialIndex,
                    localizedVertices,
                    false,
                    null,
                    preserveVertexAlpha,
                    false);

                if (primitive == null)
                    continue;

                emittedLeaf = true;
                primitive.NativeMetadata.Add(
                    Ps2WorldzoneMaterialWriter.MakePs2GsMetadata(leaf, tex0Resolver, "ps2_worldzone_leaf"));
                primitive.NativeMetadata.Add(new Ps2WorldzoneLeafRenderMetadata(
                    mdlName,
                    leafIndex,
                    space,
                    Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf).ToString(),
                    Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(leaf),
                    leaf.IsBillboard,
                    leaf.IsLocalSpace,
                    leaf.Colour,
                    leaf.Flags,
                    drawItem.DrawIndex,
                    passIndex,
                    overlapGroup,
                    blendOffset.X,
                    blendOffset.Y,
                    blendOffset.Z));
                if (leaf.BillboardDescriptor is { } billboard)
                {
                    primitive.NativeMetadata.Add(new Ps2WorldzoneBillboardMetadata(
                        billboard.Kind.ToString(),
                        billboard.Anchor.X, billboard.Anchor.Y, billboard.Anchor.Z,
                        billboard.Size.X, billboard.Size.Y,
                        billboard.PivotLocal.X, billboard.PivotLocal.Y, billboard.PivotLocal.Z,
                        billboard.Axis.X, billboard.Axis.Y, billboard.Axis.Z));
                }

                var nodePosition = position + Vector3.Transform(localOrigin, rotation);
                nodePosition *= coordinateScale;
                var nodeName = instances.Count == 1
                    ? mesh.Name
                    : $"{mesh.Name}_p{placementIndex:D4}";
                ModelDocumentGeometryAdapter.AddMeshNode(document, nodeName, mesh,
                    CreateTransform(rotation, nodePosition));
            }

            if (emittedLeaf && debugCollector != null)
            {
                var resolution = debugTex0Resolver?.Invoke(leaf.DmaTex0, leaf.GroupChecksum);
                debugCollector.AddMaterial(new Ps2GeomMaterialDebugRecord(
                    mdlName,
                    leafIndex,
                    space,
                    drawItem.DrawIndex,
                    passIndex,
                    overlapGroup,
                    leaf.IsBillboard,
                    leaf.Checksum,
                    leaf.Flags,
                    leafGates?.CreatedAtStart ?? false,
                    leafGates?.CreatedFromTod ?? 0,
                    leafGates?.CreatedFromVariable ?? 0,
                    leaf.DmaAlpha1,
                    leaf.DmaTest1,
                    leaf.DmaFrame1,
                    alphaMode,
                    Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf, leafGates).ToString(),
                    Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(leaf),
                    leaf.GroupChecksum,
                    leaf.DmaTex0,
                    textureChecksum,
                    usesSynthesizedDestinationAlpha,
                    alphaModePng != null,
                    resolution?.ResolveMode ?? "",
                    resolution?.SourceLabel ?? "",
                    resolution?.EntryLabel ?? "",
                    Ps2GeomRenderSemantics.ClassifyPortableBakeClass((byte)(leaf.DmaAlpha1 & 0xFF)),
                    leaf.Vertices.Length,
                    min,
                    max));
            }

            if (emittedLeaf &&
                textureChecksum != 0 &&
                Ps2GeomRenderSemantics.WritesFramebufferAlpha(leaf) &&
                !Ps2GeomRenderSemantics.UsesDestinationAlphaBlend((byte)(leaf.DmaAlpha1 & 0xFF)))
            {
                recentAlphaMasks[geometryKey] =
                    new Ps2DestinationAlphaMaskCandidate(geometryKey, textureChecksum, leaf);
            }
        }
    }

    private static bool ShouldIncludeWorldzoneLeaf(
        Ps2GeomLeaf leaf,
        WorldzoneTimeOfDay timeOfDay,
        Ps2WorldzoneNodeGates? gates)
    {
        if (timeOfDay is WorldzoneTimeOfDay.All)
            return true;

        var layer = Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf, gates);
        return timeOfDay is WorldzoneTimeOfDay.Night
            ? layer != Ps2GeomRenderLayer.DayOverlay
            : layer != Ps2GeomRenderLayer.NightOverlay;
    }

    /// <summary>
    ///     Story-state (createdfromvariable) sectors are inactive at load and
    ///     scripted on as the campaign progresses, so each NODEFLAG group
    ///     exports behind a default-OFF visibility group — the PSX "What If?"
    ///     precedent: geometry is never lost, one checkbox restores a state.
    ///     Returns the set of variable checksums the caller enabled.
    /// </summary>
    private static HashSet<uint> RegisterVariantVisibilityGroups(
        ModelDocument document,
        IReadOnlyDictionary<uint, Ps2WorldzoneNodeGates> nodeGates,
        IReadOnlyDictionary<string, bool>? visibilityOverrides)
    {
        var enabled = new HashSet<uint>();
        var variants = nodeGates.Values
            .Where(static gate => gate.CreatedFromVariable != 0)
            .GroupBy(static gate => gate.CreatedFromVariable)
            .OrderBy(static group => group.Key)
            .ToList();

        foreach (var variant in variants)
        {
            var resolved = QbKey.QbKey.TryResolve(variant.Key);
            var label = resolved != null
                ? resolved.StartsWith("NODEFLAG_", StringComparison.OrdinalIgnoreCase)
                    ? resolved["NODEFLAG_".Length..]
                    : resolved
                : $"0x{variant.Key:X8}";
            var id = $"thaw.variant.{(resolved ?? variant.Key.ToString("X8")).ToLowerInvariant()}";
            var isEnabled = visibilityOverrides?.TryGetValue(id, out var selected) == true && selected;
            document.VisibilityGroups.Add(new ModelVisibilityGroup
            {
                Id = id,
                Label = $"Story state: {label}",
                DefaultEnabled = false,
                IsEnabled = isEnabled,
                Source = ModelVisibilityGroupSource.AlternateGeometry,
                SourceReference =
                    $"createdfromvariable {resolved ?? $"0x{variant.Key:X8}"} ({variant.Count()} nodes)"
            });
            if (isEnabled)
                enabled.Add(variant.Key);
        }

        return enabled;
    }

    private static bool ShouldSkipWorldzoneLeaf(Ps2GeomLeaf leaf)
    {
        // Format-B billboard leaves used to be quarantined here because the static
        // export had no way to face them at the camera. They now carry a full
        // Ps2BillboardDescriptor and the Blender importer attaches a Track-To
        // constraint per billboard, so they're allowed through.
        if (leaf.IsBillboard)
            return false;

        if (leaf.Vertices.Length < 4)
            return false;

        if (leaf.Vertices.Any(static vertex => vertex.HasNormal))
            return false;

        var (min, max) = ComputeBbox(leaf.Vertices);
        var size = max - min;
        var maxDimension = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (maxDimension < 1000f)
            return false;

        var center = (min + max) * 0.5f;
        if (Math.Abs(center.X) > 10f || Math.Abs(center.Y) > 10f || Math.Abs(center.Z) > 10f)
            return false;

        var restartCount = leaf.Vertices.Count(static vertex => vertex.IsStripRestart);
        return restartCount >= Math.Max(2, leaf.Vertices.Length / 5);
    }

    private static Ps2Vertex[] LocalizePs2Vertices(Ps2Vertex[] vertices, Vector3 origin, float scale)
    {
        if (vertices.Length == 0)
            return vertices;

        var result = new Ps2Vertex[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            result[i] = CopyPs2Vertex(vertex, (vertex.Position - origin) * scale);
        }

        return result;
    }

    private static Ps2Vertex CopyPs2Vertex(Ps2Vertex vertex, Vector3 position)
    {
        return new Ps2Vertex(
            position,
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

    internal static (Vector3 Min, Vector3 Max) ComputeBbox(Ps2Vertex[] vertices)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        // Single-pass indexed min/max — a Select(v => v.Position) projection would
        // allocate an enumerator and still need this same reduction.
        for (var i = 0; i < vertices.Length; i++)
        {
            var position = vertices[i].Position;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return (min, max);
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
        return Math.Abs(normal.Y) > 0.5f && normal.Y < 0 ? -normal : normal;
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

    private static Matrix4x4 CreateTransform(Quaternion rotation, Vector3 translation)
    {
        var transform = Matrix4x4.CreateFromQuaternion(rotation);
        transform.Translation = translation;
        return transform;
    }
}
