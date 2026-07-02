namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Coarse PSX mesh/header revision inferred from bytes stored in the
///     model file. Runtime-only differences, such as Spider-Man's wider
///     subobject display masks, are tracked separately from this raw mesh
///     header classification.
/// </summary>
public enum PsxMeshFormatRevision
{
    Unknown,

    /// <summary>
    ///     Header version 0x03 without the later mesh LOD/gunk field. These
    ///     models keep raw vertex coordinates in the older Apocalypse unit
    ///     space.
    /// </summary>
    ApocalypseV3,

    /// <summary>
    ///     Header version 0x03 with the later mesh LOD/gunk field.
    /// </summary>
    NeversoftV3,

    /// <summary>
    ///     Header version 0x04, used by PS1-era Neversoft titles including
    ///     THPS, THPS2, Spider-Man, and Spider-Man 2: Enter Electro.
    /// </summary>
    NeversoftV4,

    /// <summary>
    ///     Header version 0x06, seen in later Dreamcast/PC-era PSX asset
    ///     containers derived from the PS1 format.
    /// </summary>
    NeversoftV6
}
