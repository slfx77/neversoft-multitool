namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

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

    /// <summary>
    ///     Exact source-order position pools retained by the GameCube scene
    ///     reader. Other scene readers leave this null.
    /// </summary>
    public NgcScenePositionPools? NgcPositionPools { get; init; }

    /// <summary>
    ///     The hierarchy records are authored local-to-parent placement matrices
    ///     for rigid sectors and should be applied during static export. Older
    ///     parsers leave this false until their hierarchy semantics are proven.
    /// </summary>
    public bool ApplyHierarchyTransforms { get; init; }

    public int TotalTriangles => Sectors.Sum(s => s.TotalTriangles);
    public int TotalVertices => Sectors.Sum(s => s.TotalVertices);
}
