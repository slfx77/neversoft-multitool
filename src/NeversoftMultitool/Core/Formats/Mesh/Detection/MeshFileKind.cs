namespace NeversoftMultitool.Core.Formats.Mesh.Detection;

/// <summary>
///     What kind of mesh a file is, as decided by <see cref="MeshTypeDetector" />.
///     Maps onto <see cref="Conversion.ModelSourceKind" /> for the conversion
///     pipeline, but is a separate type because detection also has to express
///     "not a mesh" and because <c>Ps2Worldzone</c> is deliberately excluded from
///     the generic candidate gate.
/// </summary>
public enum MeshFileKind
{
    /// <summary>Not a mesh file, or a mesh file whose content failed its check.</summary>
    None,

    /// <summary>COL collision (<c>.col.xbx</c>/<c>.wpc</c>/<c>.ps2</c>/<c>.psp</c>, and bare <c>.col</c>).</summary>
    Collision,

    /// <summary>PS2 scene/skin (<c>.skin.ps2</c>, <c>.mdl.ps2</c>, <c>.iskin.ps2</c>, content-resolved bare names).</summary>
    Ps2Scene,

    /// <summary>Pre-compiled PS2 CGeomNode tree (<c>.geom.ps2</c>).</summary>
    Ps2Geom,

    /// <summary>THAW PS2 worldzone PAK. Kept OUT of the generic candidate gate — see <see cref="MeshTypeDetector.IsWorldzoneCandidate" />.</summary>
    Ps2Worldzone,

    /// <summary>Xbox/PC/GameCube scene (<c>.skin</c>/<c>.mdl</c>/<c>.scn</c> x <c>.xbx</c>/<c>.wpc</c>/<c>.ngc</c>).</summary>
    XbxScene,

    /// <summary>Xbox DDM level/object geometry.</summary>
    Ddm,

    /// <summary>PS1-era PSX mesh.</summary>
    Psx,

    /// <summary>Model bundle carved from an N64 ROM (<c>models/NNN/NNN_&lt;name&gt;.psx.n64</c>).</summary>
    N64Model,

    /// <summary>RenderWare clump (<c>.skn</c>, <c>.dff</c>).</summary>
    RenderWareDff,

    /// <summary>RenderWare world (<c>.bsp</c>).</summary>
    RenderWareBsp
}
