using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
/// Parsed PS2 GEOM file (.geom.ps2).
/// Pre-compiled CGeomNode rendering tree with embedded VIF/DMA chains.
/// Contains level geometry for THPS4/THUG/THUG2.
/// </summary>
public sealed class Ps2GeomScene
{
    public required List<Ps2GeomLeaf> Leaves { get; init; }
}

/// <summary>
/// A leaf node from the CGeomNode tree containing extracted mesh geometry.
/// Vertex data is decoded from embedded VIF UNPACK instructions.
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
    /// Raw TEX0_1 GS register value extracted from the DMA chain's GS context.
    /// Contains TBP (VRAM texture base pointer), dimensions, and pixel format.
    /// Used by THPS4 where CGeomNode.texture_checksum is always 0.
    /// </summary>
    public ulong DmaTex0 { get; init; }

    /// <summary>
    /// Raw CLAMP_1 GS register value from the DMA chain.
    /// Bits 0-1: WMS (wrap S: 0=REPEAT, 1=CLAMP), bits 2-3: WMT (wrap T).
    /// </summary>
    public ulong DmaClamp1 { get; init; }

    /// <summary>
    /// Raw ALPHA_1 GS register value from the DMA chain.
    /// Encodes the alpha blending equation: Cv = ((A-B)*C)>>7 + D.
    /// Low byte encodes A,B,C,D fields; bits 32-39 = FIX value.
    /// </summary>
    public ulong DmaAlpha1 { get; init; }

    /// <summary>
    /// Raw TEST_1 GS register value from the DMA chain.
    /// Bit 0: ATE (alpha test enable), bits 1-3: ATST (method),
    /// bits 4-11: AREF (alpha reference value).
    /// </summary>
    public ulong DmaTest1 { get; init; }
}
