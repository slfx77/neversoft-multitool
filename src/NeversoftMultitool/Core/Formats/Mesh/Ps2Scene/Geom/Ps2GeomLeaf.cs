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

    /// <summary>
    ///     CGeomNode flags from offset 0x1C of the source node header. Includes
    ///     NODEFLAG_LEAF (1&lt;&lt;1) and NODEFLAG_COLOURED (1&lt;&lt;4): when the
    ///     latter is set, the runtime VU1 microcode tints all child geometry by
    ///     <see cref="Colour" /> via SetColour() in geomnode.cpp.
    /// </summary>
    public uint Flags { get; init; }

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
    ///     Raw FRAME_1 GS register value from the DMA chain. Bits 32-63 are
    ///     FBMSK, the framebuffer write mask; FBMSK[31:24] controls whether
    ///     the alpha channel is updated. Destination-alpha synthesis must
    ///     ignore mask-source draws whose framebuffer alpha writes are masked.
    /// </summary>
    public ulong DmaFrame1 { get; init; }

    /// <summary>
    ///     Raw TEXA GS register value from the DMA chain. Drives alpha
    ///     expansion for non-32-bit pixel formats — PSMCT16 textures and
    ///     paletted formats (PSMT4/PSMT8) sampling 16-bit CLUTs use TEXA
    ///     to expand the per-texel/CLUT-entry bit15 into an 8-bit alpha.
    ///     Layout: TA0(7:0) | AEM(15) | TA1(39:32). PS2 alpha range is
    ///     [0,128]; the 0xFF default the GS uses when TEXA hasn't been
    ///     written (TA0=0 TA1=128 AEM=0) is the same as the historical
    ///     "bit15 → 0/0xFF" behaviour our decoder hardcoded — but the
    ///     dump-confirmed in-game state for many draws is TA0=128 TA1=128,
    ///     which makes every 16-bit pixel fully opaque regardless of bit15.
    /// </summary>
    public ulong DmaTexa { get; init; }

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

    /// <summary>
    ///     True for synthesized THAW worldzone Format-B billboard quads. These need
    ///     cutout-style alpha in glTF so foliage billboards do not render as translucent
    ///     panes.
    /// </summary>
    public bool IsBillboard { get; init; }

    /// <summary>
    ///     Raw parametric descriptor for a Format-B billboard leaf, decoded from the four
    ///     V4_32 quadwords consumed by the VU1 billboard microprograms. Null for
    ///     non-billboard leaves. Lets the export path emit camera-facing constraints in
    ///     .blend and an axis-rotated fallback quad in .glb.
    /// </summary>
    public Ps2BillboardDescriptor? BillboardDescriptor { get; init; }
}
