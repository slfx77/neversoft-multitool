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
}
