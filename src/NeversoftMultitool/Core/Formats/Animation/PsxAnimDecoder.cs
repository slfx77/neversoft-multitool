using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Bulk-decodes one PSX animation slot (<c>boneCount × 6 channels × frameCount</c>
///     samples) into a <see cref="PsxAnimation" />. Supports both wire formats:
///     <list type="bullet">
///         <item>
///             v2 / 0x2C — per-channel <c>DecompressStream</c> blocks (the
///             default historical path; see <see cref="PsxAnimDecompressor" />).
///         </item>
///         <item>
///             v1 / 0x2A — direct uncompressed <c>SMatrix</c>: per bone per
///             frame, 9 s16 rotation-matrix cells + 3 s16 translation. The
///             rotation matrix is preserved as a quaternion and also mirrored
///             into YXZ Euler channels for existing diagnostics.
///         </item>
///     </list>
/// </summary>
public static class PsxAnimDecoder
{
    /// <summary>
    ///     Bytes the v1 direct-matrix payload spends per bone per frame: 12 s16
    ///     cells (3×3 rotation matrix + 3-vector translation).
    /// </summary>
    public const int DirectMatrixStrideBytes = 24;

    /// <summary>
    ///     Decodes the v2 compressed stream for one animation into a
    ///     fully-populated <see cref="PsxAnimation" />. The codec advances
    ///     through <paramref name="stream" /> in order: bone 0 channels 0..5,
    ///     bone 1 channels 0..5, etc.
    /// </summary>
    /// <param name="stream">Compressed bytes starting at the animation's pool offset.</param>
    /// <param name="boneCount">Number of bones (channels are decoded for each).</param>
    /// <param name="frameCount">Number of frames per channel (taken from the entry table).</param>
    /// <param name="bytesConsumed">Total bytes the codec consumed from <paramref name="stream" />.</param>
    public static PsxAnimation Decode(
        ReadOnlySpan<byte> stream, int boneCount, int frameCount, out int bytesConsumed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boneCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        var channels = new short[boneCount, PsxAnimation.ChannelsPerBone, frameCount];
        var temp = new short[frameCount];
        var consumed = 0;

        for (var b = 0; b < boneCount; b++)
        {
            for (var c = 0; c < PsxAnimation.ChannelsPerBone; c++)
            {
                if (consumed >= stream.Length)
                {
                    throw new InvalidDataException(
                        $"PSX animation stream exhausted at bone {b} channel {c}: " +
                        $"consumed {consumed} of {stream.Length}.");
                }

                var bytes = PsxAnimDecompressor.Decompress(
                    stream[consumed..], temp, 1, frameCount);
                consumed += bytes;

                for (var f = 0; f < frameCount; f++)
                    channels[b, c, f] = temp[f];
            }
        }

        bytesConsumed = consumed;
        return new PsxAnimation
        {
            FrameCount = frameCount,
            BoneCount = boneCount,
            Channels = channels,
            RotationUnitsPerRevolution = PsxAnimation.PsyqAngleUnitsPerRevolution
        };
    }

    /// <summary>
    ///     Convenience overload that discards the bytes-consumed count.
    /// </summary>
    public static PsxAnimation Decode(ReadOnlySpan<byte> stream, int boneCount, int frameCount)
    {
        return Decode(stream, boneCount, frameCount, out _);
    }

    /// <summary>
    ///     Number of SMatrix records per bone actually stored in a v1 payload.
    ///     When <paramref name="tweenFlag" /> is non-zero the payload stores only
    ///     keyframes every <c>tweenFlag + 1</c> frames (both engine call sites
    ///     pass <c>framesPerKey = tweenFlag + 1</c> to
    ///     <c>M3dUtils_InterpolateVectors</c>); intermediate frames are lerped at
    ///     runtime. The last stored record index is the largest keyframe at or
    ///     below <c>frameCount − 1</c>.
    /// </summary>
    public static int GetDirectMatrixStoredFrameCount(int frameCount, int tweenFlag)
    {
        return tweenFlag <= 0 ? frameCount : (frameCount - 1) / (tweenFlag + 1) + 1;
    }

    /// <summary>
    ///     Decodes the v1 direct-matrix payload for one animation. The payload
    ///     is laid out frame-major:
    ///     <code>[frame 0 bones, frame 1 bones, …]</code> with each bone slot
    ///     being 9 s16 rotation cells + 3 s16 translation cells = 24 bytes
    ///     (PSY-Q <c>SMatrix</c>). Rotations are preserved as matrix-derived
    ///     quaternions for export, while YXZ Euler channels are still populated
    ///     for dumps and compatibility with the v2 path.
    ///     When <paramref name="tweenFlag" /> is non-zero the payload stores only
    ///     keyframes every <c>tweenFlag + 1</c> frames; this decoder expands them
    ///     to full frames with the engine's exact truncating 1.12 lerp
    ///     (<c>M3dUtils_InterpolateVectors</c>, PERFECT-matched) before decoding.
    /// </summary>
    public static PsxAnimation DecodeDirectMatrix(
        ReadOnlySpan<byte> stream, int boneCount, int frameCount, int tweenFlag = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boneCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        if (tweenFlag > 0)
            stream = ExpandTweenKeyframes(stream, boneCount, frameCount, tweenFlag);

        var required = checked(boneCount * frameCount * DirectMatrixStrideBytes);
        if (stream.Length < required)
            throw new InvalidDataException(
                $"PSX direct-matrix payload too short: need {required} bytes, " +
                $"have {stream.Length} for {boneCount} bones × {frameCount} frames.");

        var channels = new short[boneCount, PsxAnimation.ChannelsPerBone, frameCount];
        var directRotations = new Quaternion[boneCount, frameCount];
        var perFrameStride = boneCount * DirectMatrixStrideBytes;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameBase = frame * perFrameStride;
            for (var bone = 0; bone < boneCount; bone++)
            {
                var boneBase = frameBase + bone * DirectMatrixStrideBytes;
                var matrixBytes = stream.Slice(boneBase, 18);
                directRotations[bone, frame] = DecodeSMatrixQuaternion(matrixBytes);
                var (rx, ry, rz) = ExtractYxzEulersFromSMatrix(matrixBytes);
                channels[bone, 0, frame] = RadiansToPsxAngleUnits(rx);
                channels[bone, 1, frame] = RadiansToPsxAngleUnits(ry);
                channels[bone, 2, frame] = RadiansToPsxAngleUnits(rz);

                // Translation cells: bytes 18..23 = 3 × s16.
                channels[bone, 3, frame] =
                    BinaryPrimitives.ReadInt16LittleEndian(stream.Slice(boneBase + 18, 2));
                channels[bone, 4, frame] =
                    BinaryPrimitives.ReadInt16LittleEndian(stream.Slice(boneBase + 20, 2));
                channels[bone, 5, frame] =
                    BinaryPrimitives.ReadInt16LittleEndian(stream.Slice(boneBase + 22, 2));
            }
        }

        return new PsxAnimation
        {
            FrameCount = frameCount,
            BoneCount = boneCount,
            Channels = channels,
            DirectRotations = directRotations,
            RotationUnitsPerRevolution = PsxAnimation.PsyqAngleUnitsPerRevolution
        };
    }

    /// <summary>
    ///     Expands a keyframe-reduced v1 payload to one SMatrix record per frame,
    ///     replicating <c>M3dUtils_InterpolateVectors</c> (PERFECT-matched,
    ///     thps2-psx-proto M3DUTILS.cpp): stored records sit every
    ///     <c>interval = tweenFlag + 1</c> frames; the interp factor is a
    ///     truncating 1.12 division <c>((frame − keyStart) &lt;&lt; 12) / span</c>
    ///     and each s16 cell lerps as <c>a + ((b − a) × factor &gt;&gt; 12)</c>
    ///     (GTE GPL sf=1 — truncation, no rounding). End-of-anim uses the
    ///     non-cycle branch: the window clamps back one interval and the factor
    ///     extrapolates past the last stored record, matching one-shot playback
    ///     (the cycle mode instead wraps toward frame 0 — a runtime per-instance
    ///     choice a converter cannot know; the clamp branch avoids baking a
    ///     wrap-lurch into one-shot clips, and looping viewers restart anyway).
    /// </summary>
    private static byte[] ExpandTweenKeyframes(
        ReadOnlySpan<byte> stream, int boneCount, int frameCount, int tweenFlag)
    {
        var interval = tweenFlag + 1;
        var storedFrames = GetDirectMatrixStoredFrameCount(frameCount, tweenFlag);
        var perFrame = boneCount * DirectMatrixStrideBytes;
        var storedRequired = checked(storedFrames * perFrame);
        if (stream.Length < storedRequired)
            throw new InvalidDataException(
                $"PSX direct-matrix keyframe payload too short: need {storedRequired} bytes " +
                $"({storedFrames} stored keyframes × {boneCount} bones, tween interval {interval}), " +
                $"have {stream.Length}.");

        var expanded = new byte[checked(frameCount * perFrame)];
        var shortsPerFrame = perFrame / 2;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var keyStart = frame - frame % interval;
            var keyEnd = keyStart + interval;
            int factor;
            if (keyEnd >= frameCount)
            {
                // Non-cycle end handling: clamp the window back one interval;
                // a zero keyEnd after clamping forces a straight copy of record 0.
                keyStart = Math.Max(0, keyStart - interval);
                keyEnd -= interval;
                factor = keyEnd == 0 ? 0 : ((frame - keyStart) << 12) / (keyEnd - keyStart);
            }
            else
            {
                factor = ((frame - keyStart) << 12) / (keyEnd - keyStart);
            }

            var dst = expanded.AsSpan(frame * perFrame, perFrame);
            var srcA = stream.Slice(keyStart / interval * perFrame, perFrame);
            if (factor == 0)
            {
                srcA.CopyTo(dst);
                continue;
            }

            var srcB = stream.Slice(keyEnd / interval * perFrame, perFrame);
            for (var cell = 0; cell < shortsPerFrame; cell++)
            {
                var a = BinaryPrimitives.ReadInt16LittleEndian(srcA.Slice(cell * 2, 2));
                var b = BinaryPrimitives.ReadInt16LittleEndian(srcB.Slice(cell * 2, 2));
                var value = (short)(a + (((b - a) * factor) >> 12));
                BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(cell * 2, 2), value);
            }
        }

        return expanded;
    }

    private static Quaternion DecodeSMatrixQuaternion(ReadOnlySpan<byte> matrixBytes)
    {
        var matrix = ReadSMatrixRotation(matrixBytes);
        var q = Quaternion.CreateFromRotationMatrix(matrix);
        return q.LengthSquared() > 1e-12f
            ? Quaternion.Normalize(q)
            : Quaternion.Identity;
    }

    private static Matrix4x4 ReadSMatrixRotation(ReadOnlySpan<byte> matrixBytes)
    {
        return new Matrix4x4(
            ReadSMatrixCell(matrixBytes, 0, 0),
            ReadSMatrixCell(matrixBytes, 0, 1),
            ReadSMatrixCell(matrixBytes, 0, 2),
            0f,
            ReadSMatrixCell(matrixBytes, 1, 0),
            ReadSMatrixCell(matrixBytes, 1, 1),
            ReadSMatrixCell(matrixBytes, 1, 2),
            0f,
            ReadSMatrixCell(matrixBytes, 2, 0),
            ReadSMatrixCell(matrixBytes, 2, 1),
            ReadSMatrixCell(matrixBytes, 2, 2),
            0f,
            0f,
            0f,
            0f,
            1f);
    }

    private static float ReadSMatrixCell(ReadOnlySpan<byte> matrixBytes, int row, int col)
    {
        var raw = BinaryPrimitives.ReadInt16LittleEndian(
            matrixBytes.Slice((row * 3 + col) * 2, 2));
        return raw / PsxAnimFile.DirectMatrixFixedPointDivisor;
    }

    /// <summary>
    ///     Extracts (Rx, Ry, Rz) Euler angles in radians from a PSY-Q
    ///     <c>short[3][3]</c> rotation matrix expected to be built as
    ///     <c>qy * qx * qz</c>, matching <see cref="PsxAnimation.GetBoneRotation" />.
    ///     Cells are PSY-Q fixed-point: 4096 = 1.0. In <see cref="Matrix4x4" />
    ///     terms this is the transpose of the usual column-vector
    ///     <c>Ry · Rx · Rz</c> formula, so extraction reads:
    ///     <c>M[2][1] = -sin(rx)</c>, <c>M[2][0]/M[2][2] = tan(ry)</c>,
    ///     <c>M[0][1]/M[1][1] = tan(rz)</c>.
    /// </summary>
    private static (float Rx, float Ry, float Rz) ExtractYxzEulersFromSMatrix(ReadOnlySpan<byte> matrixBytes)
    {
        // matrixBytes = 9 shorts, row-major: m[0][0..2], m[1][0..2], m[2][0..2].
        var m = new float[3, 3];
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var raw = BinaryPrimitives.ReadInt16LittleEndian(
                    matrixBytes.Slice((row * 3 + col) * 2, 2));
                m[row, col] = raw / PsxAnimFile.DirectMatrixFixedPointDivisor;
            }
        }

        // M[2][1] = -sin(rx) ⇒ rx = asin(-M[2][1]). Clamp for numerical safety.
        var sinRx = Math.Clamp(-m[2, 1], -1f, 1f);
        var rx = MathF.Asin(sinRx);

        float ry;
        float rz;
        const float gimbalThreshold = 0.99999f;
        if (MathF.Abs(sinRx) >= gimbalThreshold)
        {
            // Gimbal lock: cos(rx) ≈ 0 ⇒ ry and rz aren't separable; collapse
            // rotation into rz (matches the convention used by SharpGLTF and
            // most Euler decomposition libraries).
            ry = 0f;
            rz = MathF.Atan2(m[1, 0], m[0, 0]);
        }
        else
        {
            ry = MathF.Atan2(m[2, 0], m[2, 2]);
            rz = MathF.Atan2(m[0, 1], m[1, 1]);
        }

        return (rx, ry, rz);
    }

    /// <summary>
    ///     Converts a radian angle to the PSY-Q angle units used by
    ///     <see cref="PsxAnimation.GetBoneRotation" />. This keeps the v1
    ///     direct-matrix path consistent with the v2 compressed stream
    ///     convention consumed by the engine's <c>M3dMaths_RotMatrixYXZ</c>.
    /// </summary>
    private static short RadiansToPsxAngleUnits(float radians)
    {
        const float scale = PsxAnimation.PsyqAngleUnitsPerRevolution / (2f * MathF.PI);
        var scaled = MathF.Round(radians * scale);
        // Clamp to s16 range; sin/cos periodicity makes any 16-bit value valid.
        if (scaled >= short.MaxValue) return short.MaxValue;
        if (scaled <= short.MinValue) return short.MinValue;
        return (short)scaled;
    }
}
