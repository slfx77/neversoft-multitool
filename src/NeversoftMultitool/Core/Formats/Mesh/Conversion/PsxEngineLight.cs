using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     A PS1-era <c>M3dLight</c>: the descriptor the engine feeds to the GTE
///     when it shades a dynamically-lit model.
///
///     RE 2026-08-02 against the matched THPS2 decomp (<c>src/M3D.cpp</c>,
///     <c>M3D_owner_layout.h</c>) and confirmed by signature match in the
///     shipped binaries. <c>M3d_Render</c> takes the dynamic path when
///     <c>(pModel-&gt;Flags &amp; 4) || (pItem-&gt;Flags &amp; 0x80)</c>, loads
///     the light from <c>pItem-&gt;mpLight</c>, and calls
///     <c>M3dAsm_GetDynamicLighting</c> over the model's NORMALS.
///
///     That routine is the important part. It writes <c>0x00FFFFFF</c> — WHITE
///     — into the GTE RGB register ONCE, outside its loop, then runs
///     <c>NCCS</c> per normal. NCCS multiplies the light result by that
///     register, so with white it is a no-op: the shaded colour depends only on
///     the normal and the light, and the model's AUTHORED vertex colours are
///     never read. The same instruction sequence appears exactly once in all
///     nine PS1-era binaries (Apocalypse, THPS1 x2, THPS2 x2, Spider-Man x2,
///     SM2:EE x3 — Spider-Man final at 0x8007BFE4), so this is engine-wide
///     behaviour rather than a per-game quirk.
///
///     Scanner: <c>tools/diagnostics/psx_light_table_scan.py</c>.
/// </summary>
internal sealed record PsxEngineLight(
    Vector3[] Directions,
    Vector3[] ColorRows,
    Vector3 Ambient)
{
    private const float FixedOne = 4096f;

    private static Vector3 Fixed(int x, int y, int z)
    {
        return new Vector3(x / FixedOne, y / FixedOne, z / FixedOne);
    }

    /// <summary>
    ///     <c>M3d_DefaultLight</c> — what an item gets when nothing supplies an
    ///     owner-specific descriptor (<c>OB.cpp:233</c>), and therefore the right
    ///     default for a file converted out of context. Byte-identical in THPS2
    ///     (named by the decomp) and in Spider-Man final at 0x800A6214, which is
    ///     how it was identified there without symbols.
    ///
    ///     It is a hemisphere rig: light 1 points +Y and light 2 −Y, and PS1
    ///     space is Y-DOWN, so the cool row lights upward-facing surfaces and the
    ///     warm row lights downward-facing ones.
    /// </summary>
    internal static readonly PsxEngineLight Default = new(
        [
            Fixed(0, 0x1000, 0),
            Fixed(0, -0x1000, 0),
            Fixed(0, 0, 0x1000)
        ],
        [
            Fixed(0x1000, 0x0900, 0x0800),
            Fixed(0x0800, 0x0d00, 0x0800),
            Fixed(0x0200, 0x1000, 0x0800)
        ],
        Fixed(0x0200, 0x0200, 0x0200));

    /// <summary>
    ///     <c>Skater_DefaultLight</c> (THPS2 <c>MECH.cpp:172</c>) — the skater's
    ///     rig in ordinary levels. Mono grey, unlike the coloured default. These
    ///     are the constants previously mislabelled in this codebase as a
    ///     "front-end preview light"; they are neither front-end nor a preview.
    /// </summary>
    internal static readonly PsxEngineLight Skater = new(
        [
            Fixed(0, 0x1000, 0),
            Fixed(0, -0x1000, 0),
            Fixed(-3857, 0, 1380)
        ],
        [
            Fixed(640, 2944, 320),
            Fixed(640, 2944, 320),
            Fixed(640, 2944, 320)
        ],
        Fixed(2176, 2176, 2176));

    /// <summary>
    ///     Reproduces one GTE <c>NCCS</c> with the RGB register at white:
    ///     intensities from the three light directions (negatives clamped, the
    ///     GTE lm=1 behaviour), then ambient plus the colour matrix applied to
    ///     them. 1.0 corresponds to a full 255 channel.
    /// </summary>
    /// <param name="normal">Unit normal in the model's own (PS1) space.</param>
    internal Vector4 Evaluate(Vector3 normal)
    {
        Span<float> intensity = stackalloc float[3];
        for (var i = 0; i < 3; i++)
            intensity[i] = MathF.Max(0f, Vector3.Dot(Directions[i], normal));

        Span<float> channel = stackalloc float[3];
        for (var c = 0; c < 3; c++)
        {
            var row = ColorRows[c];
            channel[c] = AmbientChannel(c)
                         + row.X * intensity[0]
                         + row.Y * intensity[1]
                         + row.Z * intensity[2];
        }

        return new Vector4(
            Math.Clamp(channel[0], 0f, 1f),
            Math.Clamp(channel[1], 0f, 1f),
            Math.Clamp(channel[2], 0f, 1f),
            1f);
    }

    private float AmbientChannel(int index)
    {
        return index switch
        {
            0 => Ambient.X,
            1 => Ambient.Y,
            _ => Ambient.Z
        };
    }
}
