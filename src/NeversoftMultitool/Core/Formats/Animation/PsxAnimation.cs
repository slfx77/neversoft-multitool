using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Decoded PSX character animation: one slot's full <c>numBones × 6 × frameCount</c>
///     s16 sample grid, with helpers to convert per-frame values to quaternions and
///     translations consumable by glTF.
///     Channel order per bone (confirmed via THPS2 PSX prototype decompilation):
///     <list type="bullet">
///         <item>0: Rotation X (raw s16, full range = 360°)</item>
///         <item>1: Rotation Y</item>
///         <item>2: Rotation Z</item>
///         <item>3: Translation X (raw s16, scale = 1/4096)</item>
///         <item>4: Translation Y</item>
///         <item>5: Translation Z</item>
///     </list>
///     Rotation matrix is built as <c>Ry · Rx · Rz</c> (column-vector convention,
///     extrinsic ZXY / intrinsic YXZ).
/// </summary>
public sealed class PsxAnimation
{
    /// <summary>Channels per bone (Rx, Ry, Rz, Tx, Ty, Tz).</summary>
    public const int ChannelsPerBone = 6;

    // 12-bit fixed-point: 4096 = 360°. The codec writes s16 values whose low
    // 12 bits encode the angle; the high 4 bits aren't part of the angle but
    // sin/cos are periodic so multiplying the raw s16 by 2π/4096 produces the
    // correct rotation (the implicit modulo wraps the wider range to the same
    // angle).
    private const float AngleScale = (float)(2.0 * Math.PI / 4096.0);
    private const float TranslationScale = 1f / 4096f;

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
    ///     Builds the bone's rotation quaternion at <paramref name="frame" /> from
    ///     its three Euler-angle channels. Default composition (<c>YXZ</c>)
    ///     matches the PSX engine's <c>Ry · Rx · Rz</c> matrix order.
    /// </summary>
    public Quaternion GetBoneRotation(int boneIndex, int frame,
        PsxRotationCompose compose = PsxRotationCompose.YXZ)
    {
        var rx = Channels[boneIndex, 0, frame] * AngleScale;
        var ry = Channels[boneIndex, 1, frame] * AngleScale;
        var rz = Channels[boneIndex, 2, frame] * AngleScale;

        // System.Numerics quaternion mult `q1 * q2` applies q2 first, then q1.
        // For column-vector matrix product `Ry · Rx · Rz` (apply Rz first, then
        // Rx, then Ry), compose `qy * qx * qz`. Other orders are diagnostic.
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
    ///     Returns the bone's translation at <paramref name="frame" /> in PSX
    ///     world units (raw s16 / 4096, per the GTE pipeline's 12-bit fractional output).
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
}
