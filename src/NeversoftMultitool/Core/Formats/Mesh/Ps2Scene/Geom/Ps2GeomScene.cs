namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Parsed PS2 GEOM file (.geom.ps2) or PAK-extracted MDL file.
///     Pre-compiled CGeomNode rendering tree with embedded VIF/DMA chains.
///     Contains level geometry for THPS4/THUG/THUG2 and THAW PAK MDL objects.
/// </summary>
public sealed class Ps2GeomScene
{
    public required List<Ps2GeomLeaf> Leaves { get; init; }

    /// <summary>
    ///     Raw THAW PAK MDL preamble metadata. Null for standard GEOM files.
    /// </summary>
    public Ps2MdlPreamble.Preamble? MdlPreamble { get; init; }

    /// <summary>
    ///     Bone transforms parsed from <see cref="MdlPreamble" /> for compatibility with
    ///     existing callers. Null for standard GEOM files and MDLs without a parsed bone block.
    /// </summary>
    public IReadOnlyList<Ps2MdlPreamble.MdlBone>? Bones { get; init; }
}
