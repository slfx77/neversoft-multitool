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

    public static Ps2GeomScene ParsePakMdl(byte[] data)
    {
        var vifStart = Ps2GeomMdlBatchScanner.FindMdlVifStart(data);
        if (vifStart < 0)
            return new Ps2GeomScene { Leaves = [] };

        var preamble = Ps2MdlPreamble.TryParse(data, vifStart);
        var bones = preamble is { Bones: { Count: > 0 } } ? preamble.Bones : null;

        var leaves = new List<Ps2GeomLeaf>();
        var currentGsCtx = new Ps2GeomGsContext();
        var currentCenter = Vector3.Zero;
        var hasGsContext = false;

        var batchRanges = Ps2GeomMdlBatchScanner.FindMscalBatchRanges(data, vifStart, data.Length);
        var signatureBatchRanges = Ps2GeomMdlBatchScanner.FindRepeatedBatchSignatureRanges(data, vifStart, data.Length);
        if (signatureBatchRanges.Count >= 8 && signatureBatchRanges.Count > batchRanges.Count * 4)
            batchRanges = signatureBatchRanges;

        var placements = Ps2MdlPlacementResolver.ResolveObjectPlacements(preamble, batchRanges);

        // Level MDLs (no bones, many preamble records) encode each batch's world-space centre
        // in the preamble leaf records' +0x20 (x,y,z) field. Leaves are identified by
        // flag bit 1 (0x02); each leaf's +0x40 is a file offset into the VIF region that
        // identifies which batch the leaf drives. Object MDLs (with bones, few records)
        // keep the prior behaviour — centres come from in-VIF ScanBatchForCenter.
        var recordCentres = TryGetLevelBatchCentres(preamble, batchRanges);

        for (var batchIndex = 0; batchIndex < batchRanges.Count; batchIndex++)
        {
            var (batchStart, batchEnd) = batchRanges[batchIndex];
            var gsCtx = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(data, batchStart, batchEnd);
            var center = Ps2GeomMdlBatchScanner.ScanBatchForCenter(data, batchStart, batchEnd);

            if (gsCtx.HasValue)
            {
                currentGsCtx = gsCtx.Value;
                hasGsContext = true;
                currentCenter = center ?? Vector3.Zero;
            }
            else if (center.HasValue)
            {
                currentCenter = center.Value;
            }

            if (recordCentres != null && batchIndex < recordCentres.Count)
                currentCenter = recordCentres[batchIndex];

            var batchVerts = Ps2GeomVifVertexDecoder.ExtractVerticesFromVif(data, batchStart, batchEnd, currentCenter);
            if (placements.TryGetValue(batchIndex, out var placement))
                batchVerts = Ps2MdlPlacementResolver.ApplyPlacement(batchVerts, placement);
            if (batchVerts.Length == 0 || ShouldSkipWorldZoneBatch(batchVerts))
                continue;

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
                            BoundingSphere = new Vector4(sphereX, sphereY, sphereZ, sphereR),
                            Vertices = vertices,
                            DmaTex0 = gsCtx.Tex0,
                            DmaTex1 = gsCtx.Tex1,
                            DmaMipTbp1 = gsCtx.MipTbp1,
                            DmaMipTbp2 = gsCtx.MipTbp2,
                            DmaClamp1 = gsCtx.Clamp1,
                            DmaAlpha1 = gsCtx.Alpha1,
                            DmaTest1 = gsCtx.Test1
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

    private static Ps2GeomLeaf MakeLeafFromMdlMesh(IReadOnlyList<Ps2Vertex> vertices, Ps2GeomGsContext gsCtx)
    {
        var (min, max) = ComputeBbox(vertices);
        return new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = 0,
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
            IsLocalSpace = IsLocalSpaceBatch(vertices.Count, min, max),
            IsLodPlane = IsLodPlaneBatch(vertices.Count, min, max)
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
    ///     Extract per-batch world-space centres from preamble leaf records for THAW level MDLs
    ///     (the `0x7EA7357B` geometry-chunk variant). Matches each batch to the leaf records whose
    ///     <see cref="Ps2MdlPreamble.PreambleRecord.Field40" /> file offset falls inside the
    ///     batch's VIF range; the first matching leaf's <see cref="Ps2MdlPreamble.PreambleRecord.Centre" />
    ///     (verified world-space, mirrors the position the engine keeps in EE RAM) becomes the
    ///     batch placement. Returns null for object MDLs (which use bones) or when no leaves
    ///     cover the given batches.
    /// </summary>
    private static IReadOnlyList<Vector3>? TryGetLevelBatchCentres(
        Ps2MdlPreamble.Preamble? preamble, List<(int Start, int End)> batchRanges)
    {
        if (preamble is null || preamble.Bones.Count > 0 || preamble.Records.Count < 2 ||
            batchRanges.Count == 0)
            return null;

        // Object MDLs have ~10-15 records; level MDLs have thousands. Require an order-of-
        // magnitude count difference to avoid triggering on small object MDLs whose records
        // happen to include a couple of leaves.
        if (preamble.Records.Count < 100)
            return null;

        var leaves = preamble.Records.Values
            .Where(r => r.IsLeaf)
            .OrderBy(r => r.Field40)
            .ToArray();
        if (leaves.Length == 0)
            return null;

        var centres = new Vector3[batchRanges.Count];
        var found = false;
        for (var i = 0; i < batchRanges.Count; i++)
        {
            var (start, end) = batchRanges[i];
            var match = FindFirstLeafInRange(leaves, (uint)start, (uint)end);
            if (match.HasValue)
            {
                centres[i] = match.Value;
                found = true;
            }
        }

        return found ? centres : null;
    }

    /// <summary>
    ///     Binary-search variant: returns the centre of the first leaf whose
    ///     <see cref="Ps2MdlPreamble.PreambleRecord.Field40" /> falls in [start, end).
    /// </summary>
    private static Vector3? FindFirstLeafInRange(
        Ps2MdlPreamble.PreambleRecord[] leavesSortedByField40, uint start, uint end)
    {
        var lo = 0;
        var hi = leavesSortedByField40.Length - 1;
        var first = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (leavesSortedByField40[mid].Field40 >= start)
            {
                first = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        if (first < 0 || leavesSortedByField40[first].Field40 >= end)
            return null;
        return leavesSortedByField40[first].Centre;
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
        return maxDimension > 20_000f;
    }
}
