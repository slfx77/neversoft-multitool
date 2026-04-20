using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     A leaf node from the CGeomNode tree containing extracted mesh geometry.
///     Vertex data is decoded from embedded VIF UNPACK instructions.
/// </summary>
public sealed class Ps2GeomLeaf
{
    public uint Checksum { get; init; }
    public uint TextureChecksum { get; init; }
    public uint GroupChecksum { get; init; }
    public uint Colour { get; init; }
    public Vector4 BoundingSphere { get; init; }
    public required Ps2Vertex[] Vertices { get; init; }

    /// <summary>
    ///     Raw TEX0_1 GS register value extracted from the DMA chain's GS context.
    ///     Contains TBP (VRAM texture base pointer), dimensions, and pixel format.
    ///     Used by THPS4 where CGeomNode.texture_checksum is always 0.
    /// </summary>
    public ulong DmaTex0 { get; init; }

    /// <summary>
    ///     Raw TEX1_1 GS register value from the DMA chain.
    ///     Bits 2-4 encode MXL (maximum mip level); bits 6-8 encode MMIN.
    /// </summary>
    public ulong DmaTex1 { get; init; }

    /// <summary>
    ///     Raw MIPTBP1_1 GS register value from the DMA chain.
    ///     Encodes TBP/TBW pairs for mip levels 1-3.
    /// </summary>
    public ulong DmaMipTbp1 { get; init; }

    /// <summary>
    ///     Raw MIPTBP2_1 GS register value from the DMA chain.
    ///     Encodes TBP/TBW pairs for mip levels 4-6.
    /// </summary>
    public ulong DmaMipTbp2 { get; init; }

    /// <summary>
    ///     Raw CLAMP_1 GS register value from the DMA chain.
    ///     Bits 0-1: WMS (wrap S: 0=REPEAT, 1=CLAMP), bits 2-3: WMT (wrap T).
    /// </summary>
    public ulong DmaClamp1 { get; init; }

    /// <summary>
    ///     Raw ALPHA_1 GS register value from the DMA chain.
    ///     Encodes the alpha blending equation: Cv = ((A-B)*C)>>7 + D.
    ///     Low byte encodes A,B,C,D fields; bits 32-39 = FIX value.
    /// </summary>
    public ulong DmaAlpha1 { get; init; }

    /// <summary>
    ///     Raw TEST_1 GS register value from the DMA chain.
    ///     Bit 0: ATE (alpha test enable), bits 1-3: ATST (method),
    ///     bits 4-11: AREF (alpha reference value).
    /// </summary>
    public ulong DmaTest1 { get; init; }

    /// <summary>
    ///     THAW worldzone-object MDLs interleave two kinds of batches:
    ///     large "world"/infrastructure batches (shared geometry in world coordinates)
    ///     and small "local" batches (per-sector car mesh in bone-local coordinates,
    ///     meant to be rendered once per non-root bone). This flag is set by
    ///     <see cref="Ps2GeomFile.ParsePakMdl" /> when the leaf's vertex bbox
    ///     is small and centred near the origin.
    /// </summary>
    public bool IsLocalSpace { get; init; }

    /// <summary>
    ///     Heuristic flag for billboard/LOD-plane batches in worldzone object MDLs. Set when a
    ///     batch is flat (min bbox dimension &lt;&lt; max), has few vertices (typical 4-16), and is
    ///     not a tiny detail patch. Writers can skip these leaves to suppress "LOD planes cutting
    ///     through geometry" artifacts in the final glb.
    /// </summary>
    public bool IsLodPlane { get; init; }
}
