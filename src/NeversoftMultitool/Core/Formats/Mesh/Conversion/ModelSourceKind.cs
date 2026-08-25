namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public enum ModelSourceKind
{
    Generic,
    Collision,
    Ddm,
    DdmPlacedLevel,
    Psx,
    Ps2Scene,
    Ps2Geom,
    Ps2Worldzone,
    XbxScene,
    RenderWareDff,
    RenderWareBsp,

    /// <summary>
    ///     A model bundle carved from an N64 ROM: the PSX shell
    ///     (<c>models/NNN/NNN_&lt;name&gt;.psx.n64</c>) plus its <c>group2/</c> render bank.
    /// </summary>
    N64Model,

    /// <summary>
    ///     A level record carved from a Vicarious Visions GBA ROM: the engine-exact
    ///     collision surface textured with the level's own pre-baked isometric art,
    ///     resolved against the <c>rom.gbarom</c> companion.
    /// </summary>
    GbaLevel
}
