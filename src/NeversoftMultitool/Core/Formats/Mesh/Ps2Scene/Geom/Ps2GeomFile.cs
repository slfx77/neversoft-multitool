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
public static partial class Ps2GeomFile
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
