using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public static partial class Ps2GeomFile
{

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
}
