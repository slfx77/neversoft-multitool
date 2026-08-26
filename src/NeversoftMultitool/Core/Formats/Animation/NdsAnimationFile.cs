using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One keyframe of a DS animation channel: a frame time, a per-key flag, and
///     either four s16 quaternion components in 4.12 (rotation) or three fx32
///     values (translation / scale).
/// </summary>
public readonly record struct NdsAnimationKey(int Time, int Flag, int V0, int V1, int V2, int V3);

/// <summary>
///     Vicarious Visions DS animation clip — the format behind
///     <c>.\%08x.%08x.%d.animation.bin</c> (Sk8land's per-model clip library) and
///     the payloads DHJ/PG wrap in <c>comp</c> containers.
///
///     Layout (all little-endian; the offsets in the table are file-relative):
///     <code>
///     +0   u32 frames
///     +4   u32 rotationChannels
///     +8   u32 translationChannels
///     +12  u32 scaleChannels
///     +16  u32 tableEnd            // == 20 + (nRot+nTrans+nScale)*4, exact corpus-wide
///     +20  u32 channelOffset[nRot], [nTrans], [nScale]
///     </code>
///     Each channel:
///     <code>
///     +0   u16 frames              // == the clip's frames, in every shipped channel
///     +2   u16 keyCount
///     +4   u16 id                  // per-kind ordinal (redundant)
///     +6   u8  keySize             // rotation 12, translation/scale 16
///     +7   u8  pad
///     +8   u32 seekTableRel        // == 16; u32 key indices, one per 32 frames
///     +12  u32 keysRel             // keys run from here to exactly the channel end
///     </code>
///     A key is <c>{u16 time, u16 flag, payload}</c>; rotation payload is a UNIT
///     quaternion in s16 4.12 (measured: |q|² ≈ 4096² across the corpus),
///     translation/scale are fx32 triples. Channel k of a kind drives the k-th
///     joint whose record carries that kind's flag bit — the mapping is positional,
///     which is why a clip's channel counts must match the geometry's flag census
///     before it can be applied (see <c>NdsPoseScatter</c>).
///
///     The runtime evaluator is Sk8land ITCM <c>0x01FFD120</c>; it interpolates
///     between bracketing keys with the hardware divider and scatters the results
///     into the geometry's display-list matrix operands. Times are in frames, the
///     final key of every channel landing on the clip's last frame.
/// </summary>
public sealed class NdsAnimationFile
{
    private const int RotationKeySize = 12;
    private const int VectorKeySize = 16;

    private NdsAnimationFile(int frames,
        NdsAnimationKey[][] rotations, NdsAnimationKey[][] translations, NdsAnimationKey[][] scales)
    {
        Frames = frames;
        Rotations = rotations;
        Translations = translations;
        Scales = scales;
    }

    public int Frames { get; }

    /// <summary>Rotation channels, each a run of s16-quaternion keys.</summary>
    public IReadOnlyList<NdsAnimationKey[]> Rotations { get; }

    /// <summary>Translation channels, fx32 triples.</summary>
    public IReadOnlyList<NdsAnimationKey[]> Translations { get; }

    /// <summary>Scale channels, fx32 triples (4096 = 1.0).</summary>
    public IReadOnlyList<NdsAnimationKey[]> Scales { get; }

    public static bool TryParse(
        ReadOnlySpan<byte> data, [NotNullWhen(true)] out NdsAnimationFile? file)
    {
        file = null;
        if (data.Length < 24)
            return false;

        var frames = BinaryPrimitives.ReadInt32LittleEndian(data);
        var counts = new int[3];
        for (var i = 0; i < 3; i++)
            counts[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(4 + i * 4)..]);
        var tableEnd = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);

        var total = counts[0] + counts[1] + counts[2];
        if (frames <= 0 || total <= 0 || total > 4096
            || tableEnd != 20 + total * 4 || tableEnd > data.Length)
        {
            return false;
        }

        var channels = new NdsAnimationKey[3][][];
        var at = 20;
        for (var kind = 0; kind < 3; kind++)
        {
            channels[kind] = new NdsAnimationKey[counts[kind]][];
            var keySize = kind == 0 ? RotationKeySize : VectorKeySize;
            for (var c = 0; c < counts[kind]; c++, at += 4)
            {
                var offset = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
                var keys = ReadChannel(data, offset, frames, keySize);
                if (keys == null)
                    return false;
                channels[kind][c] = keys;
            }
        }

        file = new NdsAnimationFile(frames, channels[0], channels[1], channels[2]);
        return true;
    }

    private static NdsAnimationKey[]? ReadChannel(
        ReadOnlySpan<byte> data, int offset, int clipFrames, int expectedKeySize)
    {
        if (offset < 20 || offset + 16 > data.Length)
            return null;

        int frames = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        int keyCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
        int keySize = data[offset + 6];
        var keysRel = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 12)..]);

        if (frames != clipFrames || keySize != expectedKeySize || keyCount == 0)
            return null;
        var keysAt = (long)offset + keysRel;
        if (keysRel < 16 || keysAt + (long)keyCount * keySize > data.Length)
            return null;

        var keys = new NdsAnimationKey[keyCount];
        for (var k = 0; k < keyCount; k++)
        {
            var ko = (int)(keysAt + k * keySize);
            int time = BinaryPrimitives.ReadUInt16LittleEndian(data[ko..]);
            int flag = BinaryPrimitives.ReadUInt16LittleEndian(data[(ko + 2)..]);
            keys[k] = keySize == RotationKeySize
                ? new NdsAnimationKey(time, flag,
                    BinaryPrimitives.ReadInt16LittleEndian(data[(ko + 4)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[(ko + 6)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[(ko + 8)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[(ko + 10)..]))
                : new NdsAnimationKey(time, flag,
                    BinaryPrimitives.ReadInt32LittleEndian(data[(ko + 4)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(data[(ko + 8)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(data[(ko + 12)..]), 0);
        }

        return keys;
    }

    /// <summary>
    ///     The rotation of channel <paramref name="channel" /> at
    ///     <paramref name="frame" />, evaluated the runtime's way: hemisphere-corrected
    ///     COMPONENT lerp between the bracketing keys (the decompiled interpolator,
    ///     ITCM <c>0x01FFC59C</c>, negates the next key when the dot is negative and
    ///     lerps the four s16 components — nlerp, not slerp). The normalize afterwards
    ///     is a deliberate, documented deviation: the hardware feeds the slightly
    ///     short lerped quaternion straight into the unit-q matrix formula, making
    ///     mid-segment matrices microscopically non-orthonormal — negligible at 4.12
    ///     with the shipped key gaps, and glTF requires unit quaternions anyway.
    /// </summary>
    public Quaternion RotationAt(int channel, float frame)
    {
        var (a, b, t) = Bracket(Rotations[channel], frame);
        var qa = ToQuaternion(a);
        var qb = ToQuaternion(b);
        if (Quaternion.Dot(qa, qb) < 0)
            qb = new Quaternion(-qb.X, -qb.Y, -qb.Z, -qb.W);
        return Quaternion.Normalize(Quaternion.Lerp(qa, qb, t));
    }

    /// <summary>The fx32 vector of a translation channel at a frame, in world units.</summary>
    public Vector3 TranslationAt(int channel, float frame)
    {
        return VectorAt(Translations[channel], frame);
    }

    /// <summary>The fx32 vector of a scale channel at a frame (1.0 = unscaled).</summary>
    public Vector3 ScaleAt(int channel, float frame)
    {
        return VectorAt(Scales[channel], frame);
    }

    private static Vector3 VectorAt(NdsAnimationKey[] keys, float frame)
    {
        var (a, b, t) = Bracket(keys, frame);
        var va = new Vector3(a.V0, a.V1, a.V2) / 4096f;
        var vb = new Vector3(b.V0, b.V1, b.V2) / 4096f;
        return Vector3.Lerp(va, vb, t);
    }

    private static (NdsAnimationKey A, NdsAnimationKey B, float T) Bracket(
        NdsAnimationKey[] keys, float frame)
    {
        if (frame <= keys[0].Time)
            return (keys[0], keys[0], 0f);
        for (var i = 1; i < keys.Length; i++)
        {
            if (frame > keys[i].Time)
                continue;

            // Flag bit 0 on the PREVIOUS key means HOLD: the runtime's key walk
            // (ITCM 0x01FFD3B4 and siblings) refuses to take the next key when
            // `prev.flag & 1`, so the value steps rather than interpolating.
            // Lerping through a held key produces in-between poses the game
            // never displays — a skater's arm swinging THROUGH the body on its
            // way to the next keyed pose was the visible symptom. Exactly AT the
            // next key's time the walk emits that key's own value regardless,
            // hence the strict comparison.
            if (frame < keys[i].Time && (keys[i - 1].Flag & 1) != 0)
                return (keys[i - 1], keys[i - 1], 0f);

            var span = keys[i].Time - keys[i - 1].Time;
            var t = span <= 0 ? 0f : (frame - keys[i - 1].Time) / span;
            return (keys[i - 1], keys[i], t);
        }

        return (keys[^1], keys[^1], 0f);
    }

    private static Quaternion ToQuaternion(in NdsAnimationKey key)
    {
        return Quaternion.Normalize(
            new Quaternion(key.V0 / 4096f, key.V1 / 4096f, key.V2 / 4096f, key.V3 / 4096f));
    }
}
