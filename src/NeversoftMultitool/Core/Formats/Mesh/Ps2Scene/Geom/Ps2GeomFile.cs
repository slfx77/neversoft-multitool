using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Parser for PS2 GEOM files (.geom.ps2) and PAK-extracted MDL files.
///     GEOM files contain pre-compiled CGeomNode rendering trees with embedded VIF/DMA chains.
///     PAK MDL files contain the same VIF vertex format but with a variable-length preamble
///     instead of the CGeomNode tree header.
/// </summary>
public static class Ps2GeomFile
{
    private const uint NodeFlagLeaf = 1 << 1;

    public static Ps2GeomScene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static Ps2GeomScene Parse(byte[] data)
    {
        if (data.Length < 20)
            throw new InvalidDataException($"File too small: {data.Length} bytes");

        var dataSectionOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
        if (dataSectionOffset < 0 || dataSectionOffset >= data.Length)
            throw new InvalidDataException($"Invalid data section offset: 0x{dataSectionOffset:X}");

        var baseOffset = dataSectionOffset;
        var rootNodeOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(baseOffset));
        if (rootNodeOffset < 0 || baseOffset + rootNodeOffset + 80 > data.Length)
            throw new InvalidDataException($"Invalid root node offset: 0x{rootNodeOffset:X}");

        var leaves = new List<Ps2GeomLeaf>();
        WalkNodeTree(data, baseOffset, rootNodeOffset, leaves);
        return new Ps2GeomScene { Leaves = leaves };
    }

    public static bool IsPakMdl(byte[] data)
    {
        if (data.Length < 256)
            return false;

        if (ThawPs2SkinFile.IsThawPs2Skin(data, data.Length))
            return false;
        if (Ps2SceneFile.IsPs2Scene(data))
            return false;
        if (ThawPs2SkinFile.IsPakSkin(data))
            return false;

        return Ps2GeomMdlBatchScanner.FindMdlVifStart(data) >= 0;
    }

    public static Ps2GeomScene ParsePakMdl(
        byte[] data,
        string? diagnosticsName = null,
        Action<Ps2GeomLeafRejection>? rejectionLogger = null)
    {
        var vifStart = Ps2GeomMdlBatchScanner.FindMdlVifStart(data);
        if (vifStart < 0)
            return new Ps2GeomScene { Leaves = [] };

        var preamble = Ps2MdlPreamble.TryParse(data, vifStart);
        var bones = preamble is { Bones: { Count: > 0 } } ? preamble.Bones : null;

        // Level MDLs (0x7EA7357B) carry no bone section but thousands of preamble records; the
        // leaves among them drive the authoritative sub-chunk list (see ParseLevelMdlFromLeaves
        // for the field-by-field derivation). Object MDLs take the scanner path below.
        if (IsLevelMdl(preamble))
            return ParseLevelMdlFromLeaves(data, preamble!, diagnosticsName, rejectionLogger);

        return ParseObjectMdl(data, preamble, bones, vifStart, diagnosticsName, rejectionLogger);
    }

    /// <summary>
    ///     Level-MDL identification: no bones, thousands of preamble records. Object MDLs have
    ///     ~5-15 records; level MDLs have thousands (5,649 for z_bh's 003B1940.mdl).
    /// </summary>
    private static bool IsLevelMdl(Ps2MdlPreamble.Preamble? preamble)
    {
        return preamble is not null
               && preamble.Bones.Count == 0
               && preamble.Records.Count >= 100;
    }

    /// <summary>
    ///     Level-MDL path. Each preamble leaf's Field40 is a *data-section-relative* offset to
    ///     a self-contained VIF packet, NOT an absolute file offset. The leaf chunk layout (per
    ///     phase 420 decomp of FUN_001d1f58 / FUN_001d3388, verified 100% across 3,977 BH leaves
    ///     and 5 other THAW level MDLs):
    ///     [0..7]   8-byte DMA source-chain tag (QWC in low 16 bits, ID=6, ADDR=0)
    ///     [8..11]  VIF OFFSET opcode (0x02000000, immediate = 0)
    ///     [12..15] VIF STCYCL opcode (0x01000101, CL=1, WL=1)
    ///     [16..]   VIF UNPACK stream (positions, UVs, normals, colors, MSCAL)
    ///     Next leaf starts at `leaf_start + 16 + QWC*16`. The engine never submits the on-disk
    ///     DMA tag through DMAC — it reads QWC/ADDR at runtime and rebuilds a fresh DMA chain.
    ///     For our decoder, we feed the VIF bytes `[leaf_start+8, leaf_start+16+QWC*16)` to
    ///     Ps2GeomVifVertexDecoder, which handles the OFFSET/STCYCL/UNPACK stream natively.
    ///     K (data_section_offset) varies per MDL (0x110..0x10B0 observed). We derive it by
    ///     scanning for the first <c>OFFSET(0) + STCYCL(1,1)</c> pair in the low file region and
    ///     subtracting the smallest leaf Field40.
    /// </summary>
    private static Ps2GeomScene ParseLevelMdlFromLeaves(
        byte[] data,
        Ps2MdlPreamble.Preamble preamble,
        string? diagnosticsName,
        Action<Ps2GeomLeafRejection>? rejectionLogger)
    {
        var sortedLeaves = preamble.Records.Values
            .Where(r => r.IsLeaf)
            .OrderBy(r => r.Field40)
            .ToList();

        if (sortedLeaves.Count == 0)
            return new Ps2GeomScene { Leaves = [], MdlPreamble = preamble, Bones = null };

        var preambleStart = sortedLeaves.First().Offset;
        foreach (var r in preamble.Records.Values)
            preambleStart = Math.Min(preambleStart, r.Offset);

        if (!TryDeriveDataSectionOffset(data, sortedLeaves[0].Field40, preambleStart, out var k))
            return new Ps2GeomScene { Leaves = [], MdlPreamble = preamble, Bones = null };

        var outLeaves = new List<Ps2GeomLeaf>(sortedLeaves.Count);

        // GS state machine. THAW worldzone leaves emit register writes only when state
        // CHANGES from the previous draw (engine optimization). Leaves that share TEX0
        // with the previous draw write a single NOP register (reg=0x7F, value=0) as
        // padding instead. We propagate the last-seen non-empty GS context across
        // leaves so those "inheriting" leaves bind to the right texture rather than
        // rendering as untextured white.
        var inheritedGsCtx = new Ps2GeomGsContext();

        for (var i = 0; i < sortedLeaves.Count; i++)
        {
            var leaf = sortedLeaves[i];
            var absStart = k + (int)leaf.Field40;
            if (absStart < 0 || absStart + 16 > data.Length)
            {
                rejectionLogger?.Invoke(MakeRejection(
                    diagnosticsName, "parse", "invalid_leaf_offset", i, [], inheritedGsCtx.Tex0));
                continue;
            }

            var span = data.AsSpan(absStart);
            var qwc = BinaryPrimitives.ReadUInt16LittleEndian(span);
            if (qwc == 0)
            {
                rejectionLogger?.Invoke(MakeRejection(
                    diagnosticsName, "parse", "zero_qwc", i, [], inheritedGsCtx.Tex0));
                continue;
            }

            // VIF stream: OFFSET+STCYCL (8 bytes) inline after the DMA tag, then QWC more quadwords
            // following. Total VIF byte range = [absStart+8, absStart+16+QWC*16).
            var vifStreamStart = absStart + 8;
            var vifStreamEnd = absStart + 16 + qwc * 16;
            if (vifStreamEnd > data.Length)
            {
                rejectionLogger?.Invoke(MakeRejection(
                    diagnosticsName, "parse", "vif_range_past_end", i, [], inheritedGsCtx.Tex0));
                continue;
            }

            // Verify the 8 inline bytes are actually OFFSET + STCYCL. If not, this isn't a leaf
            // chunk in the expected format — skip rather than feed garbage to the decoder.
            var inlineOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            var inlineStcycl = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            if (inlineOffset != 0x02000000u || inlineStcycl != 0x01000101u)
            {
                rejectionLogger?.Invoke(MakeRejection(
                    diagnosticsName, "parse", "invalid_leaf_prologue", i, [], inheritedGsCtx.Tex0));
                continue;
            }

            // Multi-MSCAL leaves (~4% of BH's leaves) contain 2+ independent VIF batches, each
            // with its own GIF-tag UNPACK + register writes. Emitting them as a single Ps2GeomLeaf
            // would either mix textures (picking only the first batch's TEX0) or stitch phantom
            // triangles across batches. Split into one leaf per batch with per-batch GS context.
            var batches = Ps2GeomVifVertexDecoder.ExtractBatchesFromVif(
                data, vifStreamStart, vifStreamEnd, leaf.Centre);
            if (batches.Count == 0)
            {
                // Fallback: Format B billboard leaves (~5% of BH's leaves) encode V4_32 float
                // billboard parameters without STMOD. Approximate each as an axis-aligned XY
                // quad at the anchor with the recorded size so the feature is at least visible.
                var billboard = Ps2GeomVifVertexDecoder.ExtractBillboardFromVif(
                    data, vifStreamStart, vifStreamEnd);
                if (billboard is not null)
                {
                    var scanned = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(
                        data, billboard.Value.VifStart, billboard.Value.VifEnd);
                    var bbGsCtx = ResolveInheritedGsContext(inheritedGsCtx, scanned, out var updatesGsState);
                    if (updatesGsState) inheritedGsCtx = bbGsCtx;
                    outLeaves.Add(MakeLeafFromMdlMesh(
                        billboard.Value.Vertices,
                        bbGsCtx,
                        leaf.MaterialGroup,
                        true,
                        billboard.Value.Descriptor));
                }
                else
                {
                    rejectionLogger?.Invoke(MakeRejection(
                        diagnosticsName, "parse", "no_batches_or_billboard", i, [], inheritedGsCtx.Tex0));
                }

                continue;
            }

            var placement = new LeafPlacement(leaf.Centre, leaf.Size, true);
            foreach (var batch in batches)
            {
                if (batch.Vertices.Length == 0)
                {
                    rejectionLogger?.Invoke(MakeRejection(
                        diagnosticsName, "parse", "empty_batch", i, batch.Vertices, inheritedGsCtx.Tex0));
                    continue;
                }

                if (ShouldSkipWorldZoneBatch(batch.Vertices))
                {
                    rejectionLogger?.Invoke(MakeRejection(
                        diagnosticsName, "parse", "huge_origin_helper_batch", i, batch.Vertices, inheritedGsCtx.Tex0));
                    continue;
                }

                if (!IsBatchCoherent(batch.Vertices, placement))
                {
                    rejectionLogger?.Invoke(MakeRejection(
                        diagnosticsName, "parse", "incoherent_batch", i, batch.Vertices, inheritedGsCtx.Tex0));
                    continue;
                }

                var scanned = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(data, batch.VifStart, batch.VifEnd);
                var gsCtx = ResolveInheritedGsContext(inheritedGsCtx, scanned, out var updatesGsState);
                if (updatesGsState) inheritedGsCtx = gsCtx;
                outLeaves.Add(MakeLeafFromMdlMesh(batch.Vertices, gsCtx, leaf.MaterialGroup));
            }
        }

        return new Ps2GeomScene { Leaves = outLeaves, MdlPreamble = preamble, Bones = null };
    }

    private static Ps2GeomGsContext ResolveInheritedGsContext(
        Ps2GeomGsContext inherited,
        Ps2GeomGsContextScan? scanned,
        out bool updatesGsState)
    {
        if (scanned is not { HasRegisters: true } scan)
        {
            updatesGsState = false;
            return inherited;
        }

        updatesGsState = true;
        return scan.MergeWith(inherited);
    }

    /// <summary>
    ///     Scan the low file region for the first <c>OFFSET(0) + STCYCL(1,1)</c> pair — these two
    ///     VIF codes sit at bytes +8 and +12 of every level-MDL leaf's 16-byte DMA-tag preamble.
    ///     K = <c>(first_pattern_offset) - (smallest_leaf_field40)</c>. Validated by requiring the
    ///     derived K to also hit the pattern on a few additional leaves.
    /// </summary>
    private static bool TryDeriveDataSectionOffset(
        byte[] data, uint firstLeafField40, int scanLimit, out int k)
    {
        k = 0;
        var limit = Math.Min(scanLimit, data.Length) - 16;
        for (var x = 0; x < limit; x += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(x + 8)) != 0x02000000u)
                continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(x + 12)) != 0x01000101u)
                continue;

            var candidate = x - (int)firstLeafField40;
            if (candidate < 0 || candidate >= data.Length)
                continue;

            k = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Object-MDL path (THAW worldzone object MDLs — cars etc., 0x9BCC234D). These have
    ///     explicit bone sections and few preamble records; the scanner + ScanBatchForCenter
    ///     combo has historically worked for them.
    /// </summary>
    private static Ps2GeomScene ParseObjectMdl(
        byte[] data,
        Ps2MdlPreamble.Preamble? preamble,
        IReadOnlyList<Ps2MdlPreamble.MdlBone>? bones,
        int vifStart,
        string? diagnosticsName,
        Action<Ps2GeomLeafRejection>? rejectionLogger)
    {
        var leaves = new List<Ps2GeomLeaf>();
        var currentGsCtx = new Ps2GeomGsContext();
        var currentCenter = Vector3.Zero;
        var hasGsContext = false;

        var batchRanges = Ps2GeomMdlBatchScanner.FindMscalBatchRanges(data, vifStart, data.Length);
        var signatureBatchRanges = Ps2GeomMdlBatchScanner.FindRepeatedBatchSignatureRanges(data, vifStart, data.Length);
        if (signatureBatchRanges.Count >= 8 && signatureBatchRanges.Count > batchRanges.Count)
            batchRanges = signatureBatchRanges;

        var placements = Ps2MdlPlacementResolver.ResolveObjectPlacements(preamble, batchRanges);

        for (var batchIndex = 0; batchIndex < batchRanges.Count; batchIndex++)
        {
            var (batchStart, batchEnd) = batchRanges[batchIndex];
            var gsCtx = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(data, batchStart, batchEnd);
            var center = Ps2GeomMdlBatchScanner.ScanBatchForCenter(data, batchStart, batchEnd);

            if (gsCtx is { HasRegisters: true } scan)
            {
                currentGsCtx = scan.MergeWith(currentGsCtx);
                hasGsContext = true;
                currentCenter = center ?? Vector3.Zero;
            }
            else if (center.HasValue)
            {
                currentCenter = center.Value;
            }

            var batchVerts = Ps2GeomVifVertexDecoder.ExtractVerticesFromVif(data, batchStart, batchEnd, currentCenter);
            if (placements.TryGetValue(batchIndex, out var placement))
                batchVerts = Ps2MdlPlacementResolver.ApplyPlacement(batchVerts, placement);
            if (batchVerts.Length == 0)
            {
                rejectionLogger?.Invoke(MakeRejection(
                    diagnosticsName, "parse", "empty_batch", batchIndex, batchVerts, currentGsCtx.Tex0));
                continue;
            }

            if (ShouldSkipWorldZoneBatch(batchVerts))
            {
                rejectionLogger?.Invoke(MakeRejection(
                    diagnosticsName, "parse", "huge_origin_helper_batch", batchIndex, batchVerts, currentGsCtx.Tex0));
                continue;
            }

            leaves.Add(MakeLeafFromMdlMesh(batchVerts, hasGsContext ? currentGsCtx : new Ps2GeomGsContext()));
        }

        return new Ps2GeomScene { Leaves = leaves, MdlPreamble = preamble, Bones = bones };
    }

    private static void WalkNodeTree(byte[] data, int baseOffset, int rootNodeOffset, List<Ps2GeomLeaf> leaves)
    {
        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        stack.Push(rootNodeOffset);

        while (stack.Count > 0)
        {
            var nodeOffset = stack.Pop();
            if (nodeOffset == -1 || !visited.Add(nodeOffset))
                continue;

            var abs = baseOffset + nodeOffset;
            if (abs + 80 > data.Length)
                continue;

            var span = data.AsSpan(abs);
            var sphereX = BinaryPrimitives.ReadSingleLittleEndian(span);
            var sphereY = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
            var sphereZ = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
            var sphereR = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[0x1C..]);
            var u1 = BinaryPrimitives.ReadInt32LittleEndian(span[0x20..]);
            var sibling = BinaryPrimitives.ReadInt32LittleEndian(span[0x28..]);
            var groupChecksum = BinaryPrimitives.ReadUInt32LittleEndian(span[0x2C..]);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(span[0x30..]);
            var colour = BinaryPrimitives.ReadUInt32LittleEndian(span[0x3C..]);
            var textureChecksum = BinaryPrimitives.ReadUInt32LittleEndian(span[0x44..]);
            var nextLod = BinaryPrimitives.ReadInt32LittleEndian(span[0x4C..]);
            var isLeaf = (flags & NodeFlagLeaf) != 0;

            if (isLeaf && u1 != -1)
            {
                var dmaAbs = baseOffset + u1;
                if (dmaAbs + 8 <= data.Length)
                {
                    var center = new Vector3(sphereX, sphereY, sphereZ);
                    var vertices = Ps2GeomVifVertexDecoder.ExtractVerticesFromDma(data, dmaAbs, center);
                    var gsCtx = Ps2GeomVifVertexDecoder.ExtractGsContextFromDma(data, dmaAbs);

                    if (vertices.Length > 0)
                    {
                        leaves.Add(new Ps2GeomLeaf
                        {
                            Checksum = checksum,
                            TextureChecksum = textureChecksum,
                            GroupChecksum = groupChecksum,
                            Colour = colour,
                            Flags = flags,
                            BoundingSphere = new Vector4(sphereX, sphereY, sphereZ, sphereR),
                            Vertices = vertices,
                            DmaTex0 = gsCtx.Tex0,
                            DmaTex1 = gsCtx.Tex1,
                            DmaMipTbp1 = gsCtx.MipTbp1,
                            DmaMipTbp2 = gsCtx.MipTbp2,
                            DmaClamp1 = gsCtx.Clamp1,
                            DmaAlpha1 = gsCtx.Alpha1,
                            DmaTest1 = gsCtx.Test1,
                            DmaFrame1 = gsCtx.Frame1,
                            DmaTexa = gsCtx.Texa
                        });
                    }
                }
            }

            if (nextLod != -1)
                stack.Push(nextLod);
            if (sibling != -1)
                stack.Push(sibling);
            if (!isLeaf && u1 != -1)
                stack.Push(u1);
        }
    }

    private static Ps2GeomLeaf MakeLeafFromMdlMesh(
        IReadOnlyList<Ps2Vertex> vertices,
        Ps2GeomGsContext gsCtx,
        uint groupChecksum = 0,
        bool isBillboard = false,
        Ps2BillboardDescriptor? billboardDescriptor = null)
    {
        var (min, max) = ComputeBbox(vertices);
        return new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = groupChecksum,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices = vertices.ToArray(),
            DmaTex0 = gsCtx.Tex0,
            DmaTex1 = gsCtx.Tex1,
            DmaMipTbp1 = gsCtx.MipTbp1,
            DmaMipTbp2 = gsCtx.MipTbp2,
            DmaClamp1 = gsCtx.Clamp1,
            DmaAlpha1 = gsCtx.Alpha1,
            DmaTest1 = gsCtx.Test1,
            DmaFrame1 = gsCtx.Frame1,
            DmaTexa = gsCtx.Texa,
            IsLocalSpace = IsLocalSpaceBatch(vertices.Count, min, max),
            IsLodPlane = IsLodPlaneBatch(vertices.Count, min, max),
            IsBillboard = isBillboard,
            BillboardDescriptor = billboardDescriptor
        };
    }

    private static (Vector3 Min, Vector3 Max) ComputeBbox(IReadOnlyList<Ps2Vertex> vertices)
    {
        if (vertices.Count == 0)
            return (Vector3.Zero, Vector3.Zero);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in vertices)
        {
            var pos = vertex.Position;
            min = Vector3.Min(min, pos);
            max = Vector3.Max(max, pos);
        }

        return (min, max);
    }

    /// <summary>
    ///     Classify a batch as "local-space" (single-sector geometry centred at origin, meant
    ///     to be instanced per non-root bone) vs "world-space" (shared/infrastructure geometry
    ///     already positioned). Empirical thresholds calibrated against THAW z_bh/z_ho car MDLs:
    ///     car-sized batches are ~12×28×28 units centred within ±5 of origin.
    /// </summary>
    private static bool IsLocalSpaceBatch(int vertexCount, Vector3 min, Vector3 max)
    {
        if (vertexCount < 3)
            return false;
        var size = max - min;
        var center = (min + max) * 0.5f;
        var maxDim = Math.Max(size.X, Math.Max(size.Y, size.Z));
        const float MaxLocalDimension = 50f;
        const float MaxOriginOffset = 8f;
        return maxDim < MaxLocalDimension
               && Math.Abs(center.X) < MaxOriginOffset
               && Math.Abs(center.Y) < MaxOriginOffset
               && Math.Abs(center.Z) < MaxOriginOffset;
    }

    /// <summary>
    ///     Classify a batch as a billboard/LOD plane. Heuristic: very flat (thinnest axis &lt;
    ///     20% of widest), few vertices (&lt;= 16), and not a tiny detail (widest axis &gt; 20
    ///     units). These produce thin flat polygons cutting through nearby geometry when
    ///     rendered in the final glb.
    /// </summary>
    private static bool IsLodPlaneBatch(int vertexCount, Vector3 min, Vector3 max)
    {
        if (vertexCount < 3 || vertexCount > 16)
            return false;
        var size = max - min;
        var minDim = Math.Min(size.X, Math.Min(size.Y, size.Z));
        var maxDim = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (maxDim < 20f)
            return false;
        var ratio = minDim / maxDim;
        const float LodAspectThreshold = 0.2f;
        return ratio < LodAspectThreshold;
    }

    /// <summary>
    ///     Coherence filter. Decoded positions for a correctly-paired batch should cluster
    ///     inside the leaf's [centre ± size] bounding box. False-positive scanner hits — brute-
    ///     force batch starts whose data happens to contain valid-looking VIF opcodes — produce
    ///     garbage vertices that still pass the ±1M sanity envelope but land hundreds of units
    ///     away from the matched leaf's sector. Those are what generate the scattered polygons
    ///     piercing the scene. Reject any batch whose centroid is further than
    ///     max(leaf.Size * <paramref name="inflate" />, <paramref name="minMargin" />) from the
    ///     leaf centre.
    /// </summary>
    private static bool IsBatchCoherent(Ps2Vertex[] vertices, LeafPlacement placement)
    {
        if (vertices.Length == 0)
            return true;

        var sum = Vector3.Zero;
        foreach (var vertex in vertices)
            sum += vertex.Position;
        var centroid = sum / vertices.Length;

        // Matched leaves use the leaf's own Size; inherited (fill-forward) placements
        // widen the tolerance because the true sector extent is unknown.
        var inflate = placement.Matched ? 2.5f : 6f;
        var minMargin = placement.Matched ? 30f : 150f;
        var margin = Vector3.Max(placement.Size * inflate, new Vector3(minMargin));
        var delta = Vector3.Abs(centroid - placement.Centre);
        return delta.X <= margin.X && delta.Y <= margin.Y && delta.Z <= margin.Z;
    }

    private static bool ShouldSkipWorldZoneBatch(Ps2Vertex[] vertices)
    {
        if (vertices.Length > 8)
            return false;

        if (vertices.Any(vertex => vertex.HasNormal))
            return false;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in vertices.Select(static vertex => vertex.Position))
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var size = max - min;
        var maxDimension = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (maxDimension <= 20_000f)
            return false;

        var center = (min + max) * 0.5f;
        return Math.Abs(center.X) <= 10f
               && Math.Abs(center.Y) <= 10f
               && Math.Abs(center.Z) <= 10f;
    }

    private static Ps2GeomLeafRejection MakeRejection(
        string? mdlName,
        string stage,
        string reason,
        int leafIndex,
        IReadOnlyList<Ps2Vertex> vertices,
        ulong tex0)
    {
        var (min, max) = ComputeBbox(vertices);
        return new Ps2GeomLeafRejection(
            mdlName ?? "",
            stage,
            reason,
            leafIndex,
            vertices.Count,
            tex0,
            min,
            max);
    }

    /// <summary>
    ///     Placement information for one preamble-leaf sub-chunk: world-space centre (added to
    ///     each decoded sint16 position) and half-extent used by the coherence filter to reject
    ///     sub-chunks that decoded to positions far outside the expected bbox.
    /// </summary>
    private readonly record struct LeafPlacement(Vector3 Centre, Vector3 Size, bool Matched);
}
