namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

/// <summary>
///     Single-pass material in native PS2 format.
///     Unlike the cross-platform format, PS2 materials have one texture per material
///     rather than multiple rendering passes.
/// </summary>
public sealed class Ps2Material
{
    public uint Checksum { get; init; }
    public uint Flags { get; init; }
    public uint TextureChecksum { get; init; }
    public uint GroupChecksum { get; init; }
    public int AlphaRef { get; init; }
    public bool ClampU { get; init; }
    public bool ClampV { get; init; }

    /// <summary>
    ///     Raw GS ALPHA register (u64). Encodes blend equation: Output = (A-B)*C + D.
    ///     Bits 0-1: A, 2-3: B, 4-5: C, 6-7: D, 32-39: FIX.
    ///     Sources: 0=Cs(source), 1=Cd(dest), 2=0. C sources: 0=As, 1=Ad, 2=FIX.
    /// </summary>
    public ulong RegAlpha { get; init; }

    /// <summary>
    ///     Returns the fixed-blend opacity (0-1) if RegALPHA uses FIXED_BLEND mode:
    ///     (Cs-Cd)*FIX/128 + Cd (A=0,B=1,C=2,D=1). Returns null for other blend modes.
    ///     FIX=128 → 1.0 (fully opaque), FIX=0 → 0.0 (fully transparent).
    /// </summary>
    public float? FixedBlendOpacity
    {
        get
        {
            var a = (int)(RegAlpha & 0x3);
            var b = (int)((RegAlpha >> 2) & 0x3);
            var c = (int)((RegAlpha >> 4) & 0x3);
            var d = (int)((RegAlpha >> 6) & 0x3);
            var fix = (int)((RegAlpha >> 32) & 0xFF);
            // (Cs-Cd)*FIX + Cd = standard alpha blend with fixed alpha
            if (a == 0 && b == 1 && c == 2 && d == 1)
                return fix / 128f;
            return null;
        }
    }

    /// <summary>
    ///     True when the GS ALPHA blend equation resolves to an identity (fully opaque).
    ///     This happens when A==B, making the blend numerator (A-B) always zero:
    ///     Output = (A-B)*C + D = 0 + D, and with D=Cs (source), result = Cs.
    ///     Common in THAW PS2 .skin.ps2 where RegAlpha=0x00000F8000000120
    ///     (A=Cs,B=Cs,C=FIX,D=Cs) — materials use the Transparent flag for alpha-tested
    ///     texture cutout (hair, clothing edges) but the blend equation itself is opaque.
    /// </summary>
    public bool IsOpaqueBlend
    {
        get
        {
            var a = (int)(RegAlpha & 0x3);
            var b = (int)((RegAlpha >> 2) & 0x3);
            return a == b;
        }
    }
}
