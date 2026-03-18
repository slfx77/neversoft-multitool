namespace NeversoftMultitool.Core.Formats.Psx;

public sealed class RwMaterial
{
    public required byte R { get; init; }
    public required byte G { get; init; }
    public required byte B { get; init; }
    public required byte A { get; init; }
    public required string? TextureName { get; init; }
    public required string? MaskName { get; init; }
    public float Ambient { get; init; }
    public float Specular { get; init; }
    public float Diffuse { get; init; }

    /// <summary>
    ///     PS2 GS ALPHA register blend mode byte from Neversoft BSP material extension.
    ///     Decoded as: A(2)|B(2)|C(2)|D(2) where A,B,D∈{Cs,Cd,0} and C∈{As,Ad,FIX}.
    ///     Key values: 0x00=opaque, 0x44=alpha blend, 0x48=additive, 0x68=additive+FIX,
    ///     0x42=subtractive, 0x62=subtractive+FIX.
    /// </summary>
    public byte GsAlpha { get; init; }

    /// <summary>
    ///     FIX value for fixed-factor blend modes (GsAlpha 0x68, 0x62, 0x64).
    ///     Range 0-128 where 128 = full intensity.
    /// </summary>
    public byte GsAlphaFix { get; init; }

    /// <summary>True if GsAlpha indicates additive blending (Cs*factor + Cd).</summary>
    public bool IsAdditive => GsAlpha is 0x48 or 0x68;

    /// <summary>True if GsAlpha indicates subtractive blending (Cd - Cs*factor).</summary>
    public bool IsSubtractive => GsAlpha is 0x42 or 0x62;

    /// <summary>
    ///     True if the GS ALPHA formula involves Cd (destination color), meaning it's a real
    ///     blending operation. Decoded from A(2)|B(2)|C(2)|D(2) where A,B,D∈{Cs=0,Cd=1,0=2}.
    ///     False for degenerate values like 0x0A/0x20/0x2A (formula = Cs = opaque)
    ///     and invisible values like 0x80/0x8A/0xA0 (formula = 0).
    /// </summary>
    public bool IsBlend
    {
        get
        {
            if (GsAlpha == 0) return false;
            var fieldA = GsAlpha & 0x03;
            var fieldB = (GsAlpha >> 2) & 0x03;
            var fieldD = (GsAlpha >> 6) & 0x03;
            return fieldA == 1 || fieldB == 1 || fieldD == 1;
        }
    }
}
