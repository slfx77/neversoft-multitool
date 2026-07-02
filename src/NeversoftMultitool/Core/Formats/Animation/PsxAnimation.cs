using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Decoded PSX character animation: one slot's full <c>numBones × 6 × frameCount</c>
///     s16 sample grid, with helpers to convert per-frame values to quaternions and
///     translations consumable by glTF.
///     Channel order per bone (confirmed via THPS2 PSX prototype decompilation):
///     <list type="bullet">
///         <item>0: Rotation X (PSY-Q angle units, low 12 bits: 4096 = 360°)</item>
///         <item>1: Rotation Y</item>
///         <item>2: Rotation Z</item>
///         <item>3: Translation X (raw s16)</item>
///         <item>4: Translation Y</item>
///         <item>5: Translation Z</item>
///     </list>
///     The default exporter convention composes <c>qy * qx * qz</c>, matching
///     the row-major <see cref="Matrix4x4" /> convention used by the glTF path.
///     The engine's <c>M3dMaths_RotMatrixYXZ</c> masks each rotation with
///     <c>0x0fff</c> before indexing its sin/cos table.
/// </summary>
public sealed class PsxAnimation
{
    /// <summary>Channels per bone (Rx, Ry, Rz, Tx, Ty, Tz).</summary>
    public const int ChannelsPerBone = 6;

    internal const float PsyqAngleUnitsPerRevolution = 4096f;

    // The relationship between raw s16 anim translation values and the i32
    // obj.Position bind values isn't a simple constant factor across the
    // frame data — different bones diverge — so we surface raw values and
    // let the consumer decide whether to emit translation channels or skip
    // them. Rotation-only animations on a correctly-parented skeleton
    // produce stable previews; per-frame translation snapping is tracked as
    // followup work in the format reference (see psx-anim-format.md).
    private const float TranslationScale = 1f;

    /// <summary>Number of frames in this animation.</summary>
    public required int FrameCount { get; init; }

    /// <summary>Number of bones in this animation (matches the character's <c>Objects.Count</c>).</summary>
    public required int BoneCount { get; init; }

    /// <summary>
    ///     Raw s16 samples indexed as <c>[boneIndex, channel, frame]</c>. Channel
    ///     indices match the order documented on <see cref="PsxAnimation" />.
    /// </summary>
    public required short[,,] Channels { get; init; }

    /// <summary>
    ///     Optional matrix-derived rotations for v1 direct-matrix animations.
    ///     These payloads already store runtime <c>SMatrix</c> rotations, so
    ///     consumers should not need to round-trip them through Euler channels.
    /// </summary>
    public Quaternion[,]? DirectRotations { get; init; }

    /// <summary>
    ///     Angle units used by the rotation channels. PSX runtime animation
    ///     paths use PSY-Q angle units; the property remains explicit so
    ///     diagnostics can identify the convention used by decoded streams.
    /// </summary>
    public float RotationUnitsPerRevolution { get; init; } = PsyqAngleUnitsPerRevolution;

    /// <summary>
    ///     Builds the bone's rotation quaternion at <paramref name="frame" /> from
    ///     its three Euler-angle channels. Default composition (<c>YXZ</c>)
    ///     matches the PSX engine's <c>Ry · Rx · Rz</c> matrix order.
    /// </summary>
    public Quaternion GetBoneRotation(int boneIndex, int frame,
        PsxRotationCompose compose = PsxRotationCompose.YXZ,
        float rotationScale = 1f)
    {
        var scale = float.IsFinite(rotationScale) && rotationScale >= 0f
            ? rotationScale
            : 1f;

        if (TryGetDirectRotation(boneIndex, frame, scale, out var directRotation))
            return directRotation;

        var rx = ToRotationRadians(Channels[boneIndex, 0, frame]) * scale;
        var ry = ToRotationRadians(Channels[boneIndex, 1, frame]) * scale;
        var rz = ToRotationRadians(Channels[boneIndex, 2, frame]) * scale;

        // System.Numerics quaternion mult `q1 * q2` applies q2 first, then q1.
        // For the engine's YXZ path this is the original exporter convention.
        // The decompiled runtime wrapper separately confirms the PSY-Q angle
        // units/masking; its row/column matrix storage convention is handled
        // at the decoder boundary for direct-matrix payloads.
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rx);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, ry);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rz);
        return compose switch
        {
            PsxRotationCompose.YXZ => qy * qx * qz,
            PsxRotationCompose.ZXY => qz * qx * qy,
            PsxRotationCompose.XYZ => qx * qy * qz,
            PsxRotationCompose.ZYX => qz * qy * qx,
            PsxRotationCompose.XZY => qx * qz * qy,
            PsxRotationCompose.YZX => qy * qz * qx,
            _ => qy * qx * qz
        };
    }

    /// <summary>
    ///     Returns the bone's raw translation-channel values at
    ///     <paramref name="frame" />. The animation export path applies mesh
    ///     scale when deciding whether to emit translation keys.
    /// </summary>
    public Vector3 GetBoneTranslation(int boneIndex, int frame)
    {
        return new Vector3(
            Channels[boneIndex, 3, frame] * TranslationScale,
            Channels[boneIndex, 4, frame] * TranslationScale,
            Channels[boneIndex, 5, frame] * TranslationScale);
    }

    /// <summary>
    ///     Returns true when at least one rotation sample (channels 0..2) is
    ///     non-zero across all frames. False = placeholder track; the writer should
    ///     leave this bone at its bind-pose rotation rather than overriding to identity.
    /// </summary>
    public bool IsRotationAnimated(int boneIndex)
    {
        if (DirectRotations != null)
            return HasAnyDirectRotation(boneIndex);

        return HasAnyNonZero(boneIndex, 0, 3);
    }

    /// <summary>
    ///     Returns true when at least one translation sample (channels 3..5) is
    ///     non-zero across all frames. False = placeholder track; the writer should
    ///     leave this bone at its bind-pose translation rather than collapsing it
    ///     to the parent's origin.
    /// </summary>
    public bool IsTranslationAnimated(int boneIndex)
    {
        return HasAnyNonZero(boneIndex, 3, 3);
    }

    private bool HasAnyNonZero(int boneIndex, int channelStart, int channelLength)
    {
        for (var c = channelStart; c < channelStart + channelLength; c++)
        {
            for (var f = 0; f < FrameCount; f++)
            {
                if (Channels[boneIndex, c, f] != 0)
                    return true;
            }
        }

        return false;
    }

    private bool HasAnyDirectRotation(int boneIndex)
    {
        var direct = DirectRotations;
        if (direct == null || boneIndex < 0 || boneIndex >= direct.GetLength(0))
            return false;

        for (var frame = 0; frame < direct.GetLength(1); frame++)
        {
            if (!IsIdentityRotation(direct[boneIndex, frame]))
                return true;
        }

        return false;
    }

    private bool TryGetDirectRotation(
        int boneIndex,
        int frame,
        float scale,
        out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        var direct = DirectRotations;
        if (direct == null
            || boneIndex < 0
            || frame < 0
            || boneIndex >= direct.GetLength(0)
            || frame >= direct.GetLength(1))
        {
            return false;
        }

        rotation = NormalizeOrIdentity(direct[boneIndex, frame]);
        if (MathF.Abs(scale - 1f) > 0.0001f)
            rotation = NormalizeOrIdentity(Quaternion.Slerp(Quaternion.Identity, rotation, scale));
        return true;
    }

    private static bool IsIdentityRotation(Quaternion q)
    {
        q = NormalizeOrIdentity(q);
        const float epsilon = 1e-5f;
        return MathF.Abs(1f - MathF.Abs(q.W)) <= epsilon
               && MathF.Abs(q.X) <= epsilon
               && MathF.Abs(q.Y) <= epsilon
               && MathF.Abs(q.Z) <= epsilon;
    }

    private static Quaternion NormalizeOrIdentity(Quaternion q)
    {
        return q.LengthSquared() > 1e-12f
            ? Quaternion.Normalize(q)
            : Quaternion.Identity;
    }

    private float ToRotationRadians(short rawAngle)
    {
        var units = RotationUnitsPerRevolution > 0f
            ? RotationUnitsPerRevolution
            : PsyqAngleUnitsPerRevolution;
        var raw = MathF.Abs(units - PsyqAngleUnitsPerRevolution) < 0.001f
            ? rawAngle & 0x0fff
            : rawAngle;
        return raw * (2f * MathF.PI / units);
    }

}
