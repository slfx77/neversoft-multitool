namespace NeversoftMultitool.Core.Formats.Psx;

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
}
