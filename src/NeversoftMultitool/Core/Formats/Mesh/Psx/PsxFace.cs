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

    /// <summary>
    ///     Bits 7-8 (0x180): the GPU ABR blend rate, shifted into TPAGE bits
    ///     21-22 by the renderer (M3dAsm_ProcessPolys @0x80099C5C /
    ///     face_flag_semantics.md §3b) and only meaningful when the face is
    ///     semi-transparent (bit 6 sets ABE): 0 = 0.5×Back + 0.5×Front
    ///     (average), 1 = Back + Front (additive), 2 = Back − Front
    ///     (subtractive), 3 = Back + 0.25×Front. For opaque faces bit 7 is
    ///     the loader's draw-enable toggle instead, so the rate reads 0.
    ///     Corpus survey (tools/diagnostics/psx_abr_survey.py): 62.5% of
    ///     semi-transparent faces are rate 0, 24.7% rate 1, 10.1% rate 2,
    ///     2.7% rate 3.
    /// </summary>
    public int BlendRate => IsSemiTransparent ? (Flags & 0x0180) >> 7 : 0;

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

    /// <summary>
    ///     Texture scrolling/wibble metadata when this face is covered by a
    ///     tag-6 animation record. PS1 faces take their base static frame from
    ///     <see cref="PsxTextureWibbleVertex.U" /> and
    ///     <see cref="PsxTextureWibbleVertex.V" />; PC/DC v6 faces retain the
    ///     widened coordinates authored in the face payload instead.
    /// </summary>
    public PsxTextureWibble? TextureWibble { get; internal set; }

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

    internal void ApplyTextureWibble(PsxTextureWibble wibble)
    {
        TextureWibble = wibble;
        if (wibble.UsesFaceTextureCoordinates)
            return;

        for (var slot = 0; slot < Math.Min(TextureCoordinates.Length, wibble.Vertices.Length); slot++)
        {
            var vertex = wibble.Vertices[slot];
            TextureCoordinates[slot] = new PsxTextureCoordinate(vertex.U, vertex.V);
        }
    }
}
