namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     A carved GBA character: its 0x4C roster record plus the ROM companion the
///     shared skater mesh and the character's colour streams live in.
/// </summary>
public sealed record GbaModelNativeSource(
    byte[] Record,
    byte[] Rom,
    int CharacterIndex,
    string CharacterName,
    int Outfit)
    : ModelNativeSource(ModelSourceKind.GbaModel);
