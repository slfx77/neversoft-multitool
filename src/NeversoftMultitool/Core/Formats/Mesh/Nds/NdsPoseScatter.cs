using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Applies one frame of a DS animation clip to a geometry file, the way the
///     runtime does: by OVERWRITING the display list's matrix operands in place
///     (Sk8land ITCM <c>0x01FFDA6C</c>, the joint-record scatter) and letting the
///     unchanged list re-run. There is no skeleton to pose — the hierarchy is
///     compiled into the list's PUSH/MULT/POP nesting, so rewriting each joint's
///     local matrix reposes everything downstream of it for free.
///
///     Channel-to-joint mapping is positional per kind: the k-th rotation channel
///     drives the k-th joint whose record has the rotation flag, and likewise for
///     translation and scale. That is also the gate — a clip whose channel counts
///     do not match the geometry's flag census cannot be applied, and declining it
///     keeps the static document untouched.
///
///     Validated end to end before porting: frame 0 of a Sk8land skater clip
///     reproduces the shipped bind operands at vertex RMS 0.001 under exactly this
///     quaternion convention, while the transposed convention lands at 0.42.
/// </summary>
public static class NdsPoseScatter
{
    /// <summary>True when the clip's channel counts match the geometry's flag census.</summary>
    public static bool CanApply(NdsGeometryFile geometry, NdsAnimationFile clip)
    {
        if (geometry.Joints.Count != geometry.JointCount || geometry.JointCount == 0)
            return false;

        var rotation = 0;
        var translation = 0;
        var scale = 0;
        foreach (var joint in geometry.Joints)
        {
            if (joint.HasRotation)
                rotation++;
            if (joint.HasTranslation)
                translation++;
            if (joint.HasScale)
                scale++;
        }

        return rotation == clip.Rotations.Count
               && translation == clip.Translations.Count
               && scale == clip.Scales.Count;
    }

    /// <summary>
    ///     Writes the clip's pose at <paramref name="frame" /> into a copy of the
    ///     file. The caller re-runs the interpreter on the result.
    /// </summary>
    public static byte[] Apply(
        ReadOnlySpan<byte> data, NdsGeometryFile geometry, NdsAnimationFile clip, float frame)
    {
        var patched = data.ToArray();
        var rotation = 0;
        var translation = 0;
        var scale = 0;

        foreach (var joint in geometry.Joints)
        {
            Quaternion? q = joint.HasRotation ? clip.RotationAt(rotation++, frame) : null;
            Vector3? t = joint.HasTranslation ? clip.TranslationAt(translation++, frame) : null;
            Vector3? s = joint.HasScale ? clip.ScaleAt(scale++, frame) : null;

            foreach (var target in joint.Targets)
            {
                if (q.HasValue)
                    WriteRotation(patched.AsSpan(target), q.Value);
                if (t.HasValue)
                {
                    WriteVector(patched.AsSpan(target + (joint.HasRotation ? 0x24 : 0)), t.Value);
                }

                if (s.HasValue)
                {
                    // The scale triple sits after whatever else the target carries:
                    // past the 3x3 block when the joint rotates, past the translation
                    // words when it translates.
                    var at = target
                             + (joint.HasRotation ? 0x24 : 0)
                             + (joint.HasTranslation || (joint.Flags & 8) != 0 ? 0xC : 0);
                    WriteVector(patched.AsSpan(at), s.Value);
                }
            }
        }

        return patched;
    }

    /// <summary>
    ///     The 9-word 4.12 3x3 block of a MULT_4x3/MULT_3x3 operand, row-major, in
    ///     the (non-transposed) orientation the frame-0 oracle selected.
    /// </summary>
    private static void WriteRotation(Span<byte> at, Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        ReadOnlySpan<float> rows =
        [
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)
        ];
        for (var i = 0; i < 9; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                at[(i * 4)..], (int)MathF.Round(rows[i] * 4096f));
        }
    }

    private static void WriteVector(Span<byte> at, Vector3 v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(at, (int)MathF.Round(v.X * 4096f));
        BinaryPrimitives.WriteInt32LittleEndian(at[4..], (int)MathF.Round(v.Y * 4096f));
        BinaryPrimitives.WriteInt32LittleEndian(at[8..], (int)MathF.Round(v.Z * 4096f));
    }
}
