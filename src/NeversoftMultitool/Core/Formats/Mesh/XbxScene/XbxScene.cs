namespace NeversoftMultitool.Core.Formats.XbxScene;

/// <summary>
///     Parsed Xbox/PC scene file (.skin.xbx, .mdl.xbx) from THUG2.
///     Multi-pass materials, per-sector CGeom with per-mesh interleaved vertex buffers.
///     Format spec from nxtools fmt_thscene_import.py + THUG source material.cpp.
/// </summary>
public sealed class XbxScene
{
    public required XbxMaterial[] Materials { get; init; }
    public required XbxSector[] Sectors { get; init; }
    public required XbxLink[] Links { get; init; }

    public int TotalTriangles => Sectors.Sum(s => s.TotalTriangles);
    public int TotalVertices => Sectors.Sum(s => s.TotalVertices);
}
