using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

namespace WorldzoneOracleCensus;

/// <summary>
///     Re-runs the converter's ACTUAL worldzone emission decisions
///     (Ps2WorldzoneGeometryWriter.PopulatePs2WorldzoneLeaves) over a worldzone
///     pak without emitting geometry: same leaf ordering, same filter gates, the
///     real ShouldSkipRedundantWorldzoneBlendLayer predicate, and the same
///     recentAlphaMasks bookkeeping (registration gated on emitted + non-zero
///     checksum + framebuffer-alpha write + not-destination-alpha-blend).
///     Divergences from the real loop, both deliberately harmless:
///     - TryCreateSyntheticTexture is skipped. Synthesis fires only for
///       destination-alpha-blend leaves, which the mask registration below
///       excludes, so no suppression decision can change — and the RAW resolved
///       checksum is the one the GS oracle goldens are keyed by.
///     - "emitted" is decided by a triangle count that mirrors
///       AddPs2StripPrimitive's strip walk (ADC restart + degeneracy gates)
///       instead of building a ModelPrimitive.
/// </summary>
internal static class WorldzoneCensusSimulator
{
    public static WorldzoneCensusResult Run(string pakPath)
    {
        var pakBytes = File.ReadAllBytes(pakPath);
        var fullPath = Path.GetFullPath(pakPath);

        // Same catalog construction as MeshModelParser.ParsePs2Worldzone for an
        // on-disk pak with no explicit --tex: the pak itself plus sibling paks.
        ZoneTextureCatalog.TryBuild(fullPath, out var catalog);
        var fallbackTex0Resolver = catalog?.CreateTex0ChecksumResolver(fullPath);

        var mdlEntries = PakArchive.GetTypedEntries(pakBytes)
            .Where(static entry => entry.TypeHash is
                Ps2WorldzoneDetection.WorldzoneMdlTypeHash or
                Ps2WorldzoneDetection.WorldzoneLevelMdlTypeHash)
            .Select(static entry => entry.Entry)
            .ToList();

        var records = new List<LeafDecisionRecord>();
        var nearMisses = new List<(LeafDecisionRecord Leaf, BlendNearMissReason Reason)>();
        var parsed = 0;
        foreach (var mdlEntry in mdlEntries)
        {
            if (mdlEntry.Offset < 0 ||
                mdlEntry.Size <= 0 ||
                mdlEntry.Offset + mdlEntry.Size > pakBytes.Length)
            {
                continue;
            }

            var mdlData = new byte[mdlEntry.Size];
            Array.Copy(pakBytes, mdlEntry.Offset, mdlData, 0, (int)mdlEntry.Size);
            var mdlName = $"{mdlEntry.Offset:X8}";
            mdlData = Ps2WorldzoneMdlPreamble.ExtendLevelMdlPreambleIfNeeded(pakBytes, mdlEntry, mdlData);
            if (!Ps2GeomFile.IsPakMdl(mdlData))
                continue;

            var mdlTextureHint = catalog?.FindTextureEntryHintBefore(fullPath, mdlEntry.Offset) ?? fullPath;
            var mdlTex0Resolver = catalog?.CreateTex0ChecksumResolver(mdlTextureHint) ?? fallbackTex0Resolver;
            var geomScene = Ps2GeomFile.ParsePakMdl(mdlData, mdlName);
            var placements = geomScene.MdlPreamble?.Bones.Count > 0
                ? Ps2MdlPlacementResolver.ResolveWorldzonePlacements(geomScene.MdlPreamble)
                : [];
            var hasLocalPass = placements.Count > 1;
            parsed++;

            SimulatePass(records, nearMisses, geomScene, mdlName, "world", static leaf => !leaf.IsLocalSpace,
                mdlTex0Resolver);
            if (hasLocalPass)
            {
                SimulatePass(records, nearMisses, geomScene, mdlName, "local", static leaf => leaf.IsLocalSpace,
                    mdlTex0Resolver);
            }
            else
            {
                RecordUnvisitedLocalLeaves(records, geomScene, mdlName, mdlTex0Resolver);
            }
        }

        return new WorldzoneCensusResult(records, nearMisses, mdlEntries.Count, parsed, catalog != null);
    }

    private static void SimulatePass(
        List<LeafDecisionRecord> records,
        List<(LeafDecisionRecord Leaf, BlendNearMissReason Reason)> nearMisses,
        Ps2GeomScene scene,
        string mdlName,
        string space,
        Func<Ps2GeomLeaf, bool> spaceFilter,
        Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        // WorldzoneTimeOfDay defaults to All in MeshImportRequest, which makes
        // ShouldIncludeWorldzoneLeaf a constant true — the leaf filter reduces
        // to the space split.
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw(scene.Leaves);
        var recentAlphaMasks = new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>();
        // ALL emitted leaves per geometry key (checksum, blend byte, fbmsk-alpha
        // writability) — census-only bookkeeping for the near-miss classifier;
        // the converter itself only remembers the last REGISTERED mask per key.
        var emittedByKey = new Dictionary<Ps2DestinationAlphaLeafGeometryKey,
            List<(uint Checksum, byte AlphaBlend, bool WritesFbAlpha)>>();

        foreach (var drawItem in orderedLeaves)
        {
            var leaf = drawItem.Leaf;
            if (!spaceFilter(leaf))
                continue; // Belongs to the other pass; recorded there.

            if (leaf.Vertices.Length < 3)
            {
                records.Add(MakeRecord(mdlName, space, drawItem, LeafDecision.FilteredVertexCount,
                    tex0Resolver, 0, default));
                continue;
            }

            if (ShouldSkipWorldzoneLeaf(leaf))
            {
                records.Add(MakeRecord(mdlName, space, drawItem, LeafDecision.FilteredJunkGate,
                    tex0Resolver, 0, default));
                continue;
            }

            var textureChecksum = Ps2WorldzoneMaterialWriter.ResolvePs2GeomTextureChecksum(leaf, tex0Resolver);
            var geometryKey = Ps2GeomDestinationAlphaSynthesis.CreateLeafGeometryKey(leaf);
            recentAlphaMasks.TryGetValue(geometryKey, out var previousMask);
            if (Ps2WorldzoneMaterialWriter.ShouldSkipRedundantWorldzoneBlendLayer(leaf, textureChecksum,
                    geometryKey, recentAlphaMasks))
            {
                records.Add(MakeRecord(mdlName, space, drawItem, LeafDecision.Suppressed,
                    tex0Resolver, 0, previousMask));
                continue;
            }

            var triangleCount = CountStripTriangles(leaf.Vertices);
            var decision = triangleCount > 0 ? LeafDecision.Emitted : LeafDecision.EmptyStrip;
            var record = MakeRecord(mdlName, space, drawItem, decision, tex0Resolver, triangleCount, previousMask);
            records.Add(record);

            var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
            if (textureChecksum != 0 &&
                !leaf.IsBillboard &&
                Ps2GeomRenderSemantics.IsStandardSourceAlphaBlend(alphaBlend))
            {
                nearMisses.Add((record, ClassifyNearMiss(record, geometryKey, emittedByKey, recentAlphaMasks)));
            }

            if (decision == LeafDecision.Emitted)
            {
                if (textureChecksum != 0 &&
                    Ps2GeomRenderSemantics.WritesFramebufferAlpha(leaf) &&
                    !Ps2GeomRenderSemantics.UsesDestinationAlphaBlend(alphaBlend))
                {
                    recentAlphaMasks[geometryKey] =
                        new Ps2DestinationAlphaMaskCandidate(geometryKey, textureChecksum, leaf);
                }

                if (textureChecksum != 0)
                {
                    if (!emittedByKey.TryGetValue(geometryKey, out var priors))
                    {
                        priors = [];
                        emittedByKey[geometryKey] = priors;
                    }

                    priors.Add((textureChecksum, alphaBlend,
                        Ps2GeomRenderSemantics.WritesFramebufferAlpha(leaf)));
                }
            }
        }
    }

    /// <summary>
    ///     For a predicate-eligible blend leaf the filter did NOT suppress:
    ///     which clause of ShouldSkipRedundantWorldzoneBlendLayer saved it.
    /// </summary>
    private static BlendNearMissReason ClassifyNearMiss(
        LeafDecisionRecord record,
        Ps2DestinationAlphaLeafGeometryKey geometryKey,
        Dictionary<Ps2DestinationAlphaLeafGeometryKey,
            List<(uint Checksum, byte AlphaBlend, bool WritesFbAlpha)>> emittedByKey,
        Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate> recentAlphaMasks)
    {
        if (!emittedByKey.TryGetValue(geometryKey, out var priors) || priors.Count == 0)
            return BlendNearMissReason.NoPriorSameGeometry;

        var sameChecksum = priors.Where(p => p.Checksum == record.TextureChecksum).ToList();
        if (sameChecksum.Count == 0)
            return BlendNearMissReason.PriorDifferentChecksum;

        if (record.MaxDimension < 250f)
            return BlendNearMissReason.BelowDimensionThreshold;

        var opaquePriors = sameChecksum
            .Where(static p => p.AlphaBlend is 0x0A or 0x1A or 0x00)
            .ToList();
        if (opaquePriors.Count == 0)
            return BlendNearMissReason.PriorNotOpaqueWriter;

        return opaquePriors.Exists(static p => p.WritesFbAlpha) &&
               recentAlphaMasks.ContainsKey(geometryKey)
            ? BlendNearMissReason.PriorMaskOverwritten
            : BlendNearMissReason.PriorFbmskBlocked;
    }

    private static void RecordUnvisitedLocalLeaves(
        List<LeafDecisionRecord> records,
        Ps2GeomScene scene,
        string mdlName,
        Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw(scene.Leaves);
        foreach (var drawItem in orderedLeaves)
        {
            if (!drawItem.Leaf.IsLocalSpace)
                continue;

            records.Add(MakeRecord(mdlName, "local", drawItem, LeafDecision.NotVisited,
                tex0Resolver, 0, default));
        }
    }

    private static LeafDecisionRecord MakeRecord(
        string mdlName,
        string space,
        WorldzoneLeafDrawItem drawItem,
        LeafDecision decision,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        int triangleCount,
        Ps2DestinationAlphaMaskCandidate previousMask)
    {
        var leaf = drawItem.Leaf;
        var (min, max) = Ps2WorldzoneGeometryWriter.ComputeBbox(leaf.Vertices);
        var size = max - min;
        var maxDimension = leaf.Vertices.Length == 0
            ? 0f
            : Math.Max(Math.Abs(size.X), Math.Max(Math.Abs(size.Y), Math.Abs(size.Z)));
        return new LeafDecisionRecord(
            mdlName,
            space,
            drawItem.LeafIndex,
            drawItem.DrawIndex,
            Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(leaf),
            decision,
            Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf),
            (byte)(leaf.DmaAlpha1 & 0xFF),
            leaf.DmaAlpha1,
            leaf.DmaTest1,
            Ps2WorldzoneMaterialWriter.ResolvePs2GeomTextureChecksum(leaf, tex0Resolver),
            leaf.GroupChecksum,
            leaf.Vertices.Length,
            triangleCount,
            maxDimension,
            Ps2GeomDestinationAlphaSynthesis.CreateLeafGeometryKey(leaf),
            previousMask.TextureChecksum,
            previousMask.Leaf is null ? (byte)0 : (byte)(previousMask.Leaf.DmaAlpha1 & 0xFF));
    }

    /// <summary>
    ///     Verbatim copy of Ps2WorldzoneGeometryWriter.ShouldSkipWorldzoneLeaf
    ///     (private in the converter): the junk-geometry gate for huge
    ///     origin-centred normal-less strip tangles.
    /// </summary>
    private static bool ShouldSkipWorldzoneLeaf(Ps2GeomLeaf leaf)
    {
        if (leaf.IsBillboard)
            return false;

        if (leaf.Vertices.Length < 4)
            return false;

        if (leaf.Vertices.Any(static vertex => vertex.HasNormal))
            return false;

        var (min, max) = Ps2WorldzoneGeometryWriter.ComputeBbox(leaf.Vertices);
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

    /// <summary>
    ///     Mirrors the triangle-collection walk of
    ///     Ps2SceneGeometryWriter.AddPs2StripPrimitive for the worldzone call
    ///     shape (parity bias 0, no dedup): ADC restart suppresses only the
    ///     triangle ending at the restart vertex, degenerate triangles drop.
    ///     AddPrimitive returns null exactly when this count is zero.
    /// </summary>
    private static int CountStripTriangles(Ps2Vertex[] sourceVertices)
    {
        var count = 0;
        for (var i = 2; i < sourceVertices.Length; i++)
        {
            if (sourceVertices[i].IsStripRestart)
                continue;

            if (!ModelDocumentGeometryAdapter.IsDegenerate(
                    sourceVertices[i - 2].Position,
                    sourceVertices[i - 1].Position,
                    sourceVertices[i].Position))
            {
                count++;
            }
        }

        return count;
    }
}
