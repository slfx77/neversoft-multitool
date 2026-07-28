using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace WorldzoneOracleCensus;

/// <summary>
///     What the converter's worldzone emission loop decided for one leaf in one
///     pass (world/local) of one MDL entry.
/// </summary>
internal enum LeafDecision
{
    /// <summary>Survived every gate and produced at least one triangle.</summary>
    Emitted,

    /// <summary>Survived every gate but the strip produced zero triangles.</summary>
    EmptyStrip,

    /// <summary>Dropped by ShouldSkipRedundantWorldzoneBlendLayer (default-keep filter).</summary>
    Suppressed,

    /// <summary>Dropped by the Vertices.Length &lt; 3 gate.</summary>
    FilteredVertexCount,

    /// <summary>Dropped by the private ShouldSkipWorldzoneLeaf junk-geometry gate.</summary>
    FilteredJunkGate,

    /// <summary>Local-space leaf in an MDL with no bone placements — the local pass never runs.</summary>
    NotVisited
}

/// <summary>One row of the census: a leaf visit plus the converter's decision inputs.</summary>
internal sealed record LeafDecisionRecord(
    string MdlName,
    string Space,
    int LeafIndex,
    int DrawIndex,
    uint RenderOrderKey,
    LeafDecision Decision,
    string AlphaMode,
    byte AlphaBlend,
    ulong DmaAlpha1,
    ulong DmaTest1,
    uint TextureChecksum,
    uint GroupChecksum,
    int VertexCount,
    int TriangleCount,
    float MaxDimension,
    Ps2DestinationAlphaLeafGeometryKey GeometryKey,
    uint PreviousMaskChecksum,
    byte PreviousMaskAlphaBlend)
{
    public uint AlphaA => (uint)(AlphaBlend & 0x03);
    public uint AlphaB => (uint)((AlphaBlend >> 2) & 0x03);
    public uint AlphaC => (uint)((AlphaBlend >> 4) & 0x03);
    public uint AlphaD => (uint)((AlphaBlend >> 6) & 0x03);
}

/// <summary>
///     Why a predicate-eligible standard-source-alpha-blend leaf was NOT
///     suppressed — the census evidence for how selective the current
///     default-keep filter actually is.
/// </summary>
internal enum BlendNearMissReason
{
    /// <summary>No previously emitted leaf shares this leaf's geometry key.</summary>
    NoPriorSameGeometry,

    /// <summary>Same-geometry priors exist but none share the texture checksum.</summary>
    PriorDifferentChecksum,

    /// <summary>Same-checksum prior exists but this leaf is under the 250-unit dimension floor.</summary>
    BelowDimensionThreshold,

    /// <summary>Same-checksum priors exist but none is an opaque-equivalent writer (0x00/0x0A/0x1A).</summary>
    PriorNotOpaqueWriter,

    /// <summary>An eligible opaque prior exists but its FBMSK masks alpha writes, so it never registered.</summary>
    PriorFbmskBlocked,

    /// <summary>An eligible opaque prior registered but a later leaf overwrote the geometry-key slot.</summary>
    PriorMaskOverwritten
}

/// <summary>Everything the simulator learned from one worldzone pak.</summary>
internal sealed record WorldzoneCensusResult(
    List<LeafDecisionRecord> Records,
    List<(LeafDecisionRecord Leaf, BlendNearMissReason Reason)> BlendNearMisses,
    int MdlEntryCount,
    int MdlParsedCount,
    bool CatalogBuilt);
