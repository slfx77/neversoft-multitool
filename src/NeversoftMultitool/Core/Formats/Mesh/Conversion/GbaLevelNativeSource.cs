namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     A carved GBA level: its 0x15C table record plus the ROM it dereferences into
///     (the <c>rom.gbarom</c> companion). Geometry is the engine-exact collision
///     surface; the texture is the level's own pre-baked isometric art.
/// </summary>
public sealed record GbaLevelNativeSource(
    byte[] Record,
    byte[] Rom,
    int TrueRecordOffset,
    string LevelName,
    string LevelLocation)
    : ModelNativeSource(ModelSourceKind.GbaLevel);
