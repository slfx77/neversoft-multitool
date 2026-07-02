using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

/// <summary>
///     Per-vertex lighting model approximating the PS2 VU1 microcode's
///     `ambient + N·L_sun · sun_color` formula. Applied at parse time so the
///     pre-baked source vertex colours match what PS2 actually outputs to GS.
///     Without this the worldzone renders ~2x too bright: source VC for
///     surfaces like the asphalt is "neutral 128" (full source brightness),
///     and PS2 darkens it at runtime via the vertex shader. Defaults derived
///     from GS-dump observation of z_sm: asphalt-shadow modulation factor
///     is approximately (0.28, 0.24, 0.20) (pure ambient, sun occluded by
///     buildings); fully sun-lit surfaces saturate at ~1.0.
/// </summary>
public readonly record struct Ps2WorldzoneLighting(
    Vector3 Ambient,
    Vector3 SunDirection,
    Vector3 SunColor)
{
    public static Ps2WorldzoneLighting Default => new(
        new Vector3(0.30f, 0.27f, 0.24f),
        Vector3.Normalize(new Vector3(0.4f, 0.7f, 0.6f)),
        new Vector3(0.65f, 0.65f, 0.62f));
}
