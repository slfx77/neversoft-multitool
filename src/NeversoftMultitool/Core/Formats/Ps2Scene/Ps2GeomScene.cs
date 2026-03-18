namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parsed PS2 GEOM file (.geom.ps2).
///     Pre-compiled CGeomNode rendering tree with embedded VIF/DMA chains.
///     Contains level geometry for THPS4/THUG/THUG2.
/// </summary>
public sealed class Ps2GeomScene
{
    public required List<Ps2GeomLeaf> Leaves { get; init; }
}
