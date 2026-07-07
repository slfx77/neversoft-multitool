namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     A face (primitive) in a PSX mesh. Can be a triangle or quad.
/// </summary>
public sealed class PsxFace
{
    public ushort Flags { get; init; }
    public bool IsQuad { get; init; }
    public bool IsTextured { get; init; }
    public bool IsGouraud { get; init; }
    public bool IsSemiTransparent { get; init; }

    /// <summary>
    ///     Bit 9 (0x200): double-sided / disable backface culling. The engine
    ///     backface-culls every face whose bit 9 is clear
    ///     (M3dAsm_ProcessPolys @0x80099B04) — so exported materials must be
    ///     single-sided unless this bit is set, or viewers render back faces
    ///     the game never shows (skin through sleeves etc.).
    /// </summary>
    public bool IsDoubleSided => (Flags & 0x0200) != 0;

    public uint Index0 { get; init; }
    public uint Index1 { get; init; }
    public uint Index2 { get; init; }
    public uint Index3 { get; init; }
    public uint NormalIndex { get; init; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public byte Mode { get; init; }
    public uint TextureHash { get; init; }
    public byte U0 { get; init; }
    public byte V0 { get; init; }
    public byte U1 { get; init; }
    public byte V1 { get; init; }
    public byte U2 { get; init; }
    public byte V2 { get; init; }
    public byte U3 { get; init; }
    public byte V3 { get; init; }

    internal PsxTextureCoordinate[] TextureCoordinates { get; init; } =
    [
        default,
        default,
        default,
        default
    ];

    internal PsxTextureCoordinate GetTextureCoordinate(int slot)
    {
        return TextureCoordinates[slot];
    }
}
