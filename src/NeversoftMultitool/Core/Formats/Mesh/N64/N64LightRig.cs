using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     The single libultra <c>Lights1</c> rig each N64 port uploads once at
///     startup and never rewrites — one MONOCHROME grey directional light plus a
///     grey ambient.
///     <para>
///         Located 2026-08-07 by finding the static setup display list that is
///         byte-identical in shape across all four ROMs:
///         <code>
///         D9FFFFFF 00200405   G_GEOMETRYMODE  set G_SHADE|G_ZBUFFER|G_CULL_BACK|G_SHADING_SMOOTH
///         DB020000 00000018   gSPNumLights(1)
///         DC08060A &lt;ptr&gt;      gSPLight(&amp;rig.l[0], 1)
///         DC08090A &lt;ptr&gt;      gSPLight(&amp;rig.a,    2)
///         DF000000 00000000   G_ENDDL
///         </code>
///         The pointers land on ordinary initialised data — an
///         <c>Ambient_t</c> followed by a <c>Light_t</c> — and NO code
///         references either body, so the shipped bytes are exactly what the RSP
///         receives. Measured rigs: THPS1 ambient (95,95,95), light (120,120,120)
///         from (73,73,73); THPS2/THPS3/Spider-Man ambient (70,70,70), light
///         (105,105,105) from (0,-127,0).
///     </para>
///     <para>
///         This is read from the ROM rather than tabled per game, because the
///         two rigs differ and a table would silently mis-shade any build not in
///         it. The PS1 <c>M3d_DefaultLight</c> hemisphere was NOT ported — there
///         is no three-light rig and no colour matrix in any of these images, so
///         do not go looking for one.
///     </para>
///     <para>
///         The consequence that matters: shading is
///         <c>ambient + colour · max(0, N·L)</c> per channel, monochrome, so a
///         lit vertex spans grey [70,175] (THPS2/3/SM) or [95,215] (THPS1) out
///         of 255. It can never be coloured and can never reach white — which is
///         why exporting lit pools as pure white was wrong in kind, not degree.
///     </para>
/// </summary>
public sealed record N64LightRig(Vector3 Ambient, Vector3 Colour, Vector3 Direction)
{
    private const uint NumLightsCommand = 0xDB02_0000;
    private const uint NumLightsOneArgument = 0x0000_0018;
    private const uint LightSlotCommand = 0xDC08_060A;
    private const uint AmbientSlotCommand = 0xDC08_090A;

    /// <summary>
    ///     How far ahead of the setup display list to look for the rig body.
    ///     Measured at exactly 0xB0 BEFORE the list in all four ROMs; the window
    ///     is generous so a build that lays it out slightly differently is still
    ///     found, and the match is required to be unique rather than nearest.
    /// </summary>
    private const int BodySearchWindow = 0x400;

    /// <summary>
    ///     Evaluates the rig for one normal, in the 0..1 domain, exactly as the
    ///     RSP does: ambient plus the directional term clamped at zero. The
    ///     normal is expected in the same space the pool stores it.
    ///     <para>
    ///         A DEGENERATE (all-zero) normal therefore lands on pure ambient,
    ///         which is the measured hardware result rather than a fallback —
    ///         112 groups corpus-wide store literal <c>00 00 00 FF</c> vertices,
    ///         among them THPS1's taxi body and wheels.
    ///     </para>
    /// </summary>
    public Vector3 Shade(Vector3 normal)
    {
        var lambert = normal.LengthSquared() > 1e-9f
            ? MathF.Max(0f, Vector3.Dot(Vector3.Normalize(normal), Direction))
            : 0f;
        var shaded = Ambient + Colour * lambert;
        return Vector3.Clamp(shaded, Vector3.Zero, Vector3.One);
    }

    /// <summary>
    ///     Reads the rig out of a carved <c>boot.bin</c>, or null when the setup
    ///     display list is absent (an unknown build, or a truncated image).
    /// </summary>
    public static N64LightRig? TryParse(ReadOnlySpan<byte> boot)
    {
        // The setup DL is unique: exactly one aligned occurrence of each of the
        // three command words per image, eight bytes apart.
        for (var offset = 0; offset + 24 <= boot.Length; offset += 4)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(boot[offset..]) != NumLightsCommand)
                continue;
            if (BinaryPrimitives.ReadUInt32BigEndian(boot[(offset + 4)..]) != NumLightsOneArgument)
                continue;
            if (BinaryPrimitives.ReadUInt32BigEndian(boot[(offset + 8)..]) != LightSlotCommand)
                continue;
            if (BinaryPrimitives.ReadUInt32BigEndian(boot[(offset + 16)..]) != AmbientSlotCommand)
                continue;

            var lightPointer = BinaryPrimitives.ReadUInt32BigEndian(boot[(offset + 12)..]);
            var ambientPointer = BinaryPrimitives.ReadUInt32BigEndian(boot[(offset + 20)..]);
            if (lightPointer != ambientPointer + 8)
                continue;

            return TryLocateBody(boot, offset);
        }

        return null;
    }

    /// <summary>
    ///     Finds the rig body near the setup display list. The pointers in the
    ///     list are RAM addresses and the image's load base is NOT a round
    ///     number (THPS1's is 0x80016990), so resolving them arithmetically
    ///     needs a base this file cannot know. Locality is the reliable handle
    ///     instead: the body sits 0xB0 bytes ahead of the list in every ROM.
    ///     <para>
    ///         The match must be UNIQUE within the window, and a rig whose light
    ///         colour is zero is rejected — an all-zero Light_t contributes
    ///         nothing and cannot be the active rig. Without that rejection the
    ///         window admits a decoy 0x10 further out in THPS2, THPS3 and
    ///         Spider-Man, which is really the true rig read at a shifted
    ///         offset. Ambiguity returns null rather than a guess.
    ///     </para>
    /// </summary>
    private static N64LightRig? TryLocateBody(ReadOnlySpan<byte> boot, int displayListOffset)
    {
        var from = Math.Max(0, displayListOffset - BodySearchWindow);
        N64LightRig? found = null;
        for (var offset = from; offset + 24 <= displayListOffset; offset += 4)
        {
            var rig = TryDecodeBody(boot[offset..]);
            if (rig == null)
                continue;
            if (found != null)
                return null;
            found = rig;
        }

        return found;
    }

    /// <summary>
    ///     Decodes Ambient_t (u8 col[3], pad, u8 colc[3], pad) followed by
    ///     Light_t (the same eight bytes, then s8 dir[3], pad). Both structures
    ///     duplicate their colour into colc, which is what makes the shape
    ///     recognisable.
    /// </summary>
    private static N64LightRig? TryDecodeBody(ReadOnlySpan<byte> body)
    {
        if (body[3] != 0 || body[7] != 0 || body[11] != 0 || body[19] != 0)
            return null;
        if (body[0] != body[4] || body[1] != body[5] || body[2] != body[6])
            return null;
        if (body[8] != body[12] || body[9] != body[13] || body[10] != body[14])
            return null;

        var colour = new Vector3(body[8], body[9], body[10]);
        if (colour.LengthSquared() < 1e-6f)
            return null;

        var direction = new Vector3((sbyte)body[16], (sbyte)body[17], (sbyte)body[18]);
        var length = direction.Length();
        if (length is < 120f or > 134f)
            return null;

        return new N64LightRig(
            new Vector3(body[0], body[1], body[2]) / 255f,
            colour / 255f,
            direction / length);
    }
}
