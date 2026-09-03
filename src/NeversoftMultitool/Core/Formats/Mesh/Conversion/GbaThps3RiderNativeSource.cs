namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     A carved THPS3 GBA rider: its 0x14-byte directory record plus the ROM
///     companion the mesh, pose bank and clip tables live in. Shares the
///     <see cref="ModelSourceKind.GbaModel" /> kind with the THPS2 skater — the
///     parser tells the two apart by record length.
/// </summary>
public sealed record GbaThps3RiderNativeSource(byte[] Record, byte[] Rom)
    : ModelNativeSource(ModelSourceKind.GbaModel);
