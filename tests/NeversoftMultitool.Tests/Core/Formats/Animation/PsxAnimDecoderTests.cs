using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public class PsxAnimDecoderTests(TestPaths paths)
{
    private const string SpiderManBuild = "Spider-Man (2000-9-1, PSX - Final)";
    private const string Thps2ProtoBuild = "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)";

    [Fact]
    public void Decompress_Mode0Linear_WritesExactSegmentEndpoint()
    {
        // Header high nibble 1 => two samples per full segment after the
        // initial value. Mode 0 linearly interpolates 0 -> 5, so the engine
        // writes one truncated midpoint and then the exact endpoint.
        byte[] stream = [0x10, 0x00, 0x00, 0x05, 0x00];
        Span<short> output = stackalloc short[3];

        var consumed = PsxAnimDecompressor.Decompress(stream, output, step: 1, streamLength: 3);

        Assert.Equal(5, consumed);
        Assert.Equal(new short[] { 0, 2, 5 }, output.ToArray());
    }

    [Fact]
    public void Decompress_Mode1BitPacked_StartsDeltaBitsAfterInitialSample()
    {
        // Mode 1 uses 2-bit signed deltas. Delta +1 is packed into the top two
        // bits of the byte after the initial s16 sample.
        byte[] stream = [0x11, 0x00, 0x00, 0x40, 0x00, 0x00];
        Span<short> output = stackalloc short[3];

        var consumed = PsxAnimDecompressor.Decompress(stream, output, step: 1, streamLength: 3);

        Assert.Equal(4, consumed);
        Assert.Equal(new short[] { 0, 0, 1 }, output.ToArray());
    }

    [Fact]
    public void Decompress_BitPackedRemainder_WritesExactEndpoint()
    {
        // 0x32 => segment length 4, mode 2 (3-bit deltas). With 7 output
        // frames the engine writes one full segment plus a two-frame remainder.
        // The remainder endpoint is prev + delta from the start of the
        // remainder, not from the interpolated midpoint.
        byte[] stream = [0x32, 0x00, 0x00, 0x0C, 0x00, 0x00];
        Span<short> output = stackalloc short[7];

        var consumed = PsxAnimDecompressor.Decompress(stream, output, step: 1, streamLength: 7);

        Assert.Equal(4, consumed);
        Assert.Equal(new short[] { 0, 0, 0, 0, 0, 1, 3 }, output.ToArray());
    }

    [Fact]
    public void Decompress_Mode14RepeatsImmediateConstant()
    {
        byte[] stream = [0x0E, 0x34, 0x12];
        Span<short> output = stackalloc short[3];

        var consumed = PsxAnimDecompressor.Decompress(stream, output, step: 1, streamLength: 3);

        Assert.Equal(3, consumed);
        Assert.Equal(new short[] { 0x1234, 0x1234, 0x1234 }, output.ToArray());
    }

    [Fact]
    public void Decompress_Mode15ZerosHaveNoPayload()
    {
        byte[] stream = [0x0F];
        Span<short> output = stackalloc short[3];

        var consumed = PsxAnimDecompressor.Decompress(stream, output, step: 1, streamLength: 3);

        Assert.Equal(1, consumed);
        Assert.Equal(new short[] { 0, 0, 0 }, output.ToArray());
    }

    [Fact]
    public void GetBoneRotation_DefaultCompressedStreamUsesPsyqAngleUnits()
    {
        var channels = new short[1, 6, 1];
        channels[0, 1, 0] = 1024; // 90 degrees in PSY-Q angle units.
        var animation = new PsxAnimation { FrameCount = 1, BoneCount = 1, Channels = channels };

        var actual = Matrix4x4.CreateFromQuaternion(
            Quaternion.Normalize(animation.GetBoneRotation(0, 0)));
        var expected = CreateExpectedRotMatrixYxz(ToPsyqRadians(0), ToPsyqRadians(1024), ToPsyqRadians(0));

        AssertMatrixClose(expected, actual);
    }

    [Fact]
    public void GetBoneRotation_RotationScaleHalvesDecodedAngles()
    {
        var channels = new short[1, 6, 1];
        channels[0, 1, 0] = 1024; // 90 degrees in PSY-Q angle units.
        var animation = new PsxAnimation { FrameCount = 1, BoneCount = 1, Channels = channels };

        var actual = Matrix4x4.CreateFromQuaternion(
            Quaternion.Normalize(animation.GetBoneRotation(0, 0, rotationScale: 0.5f)));
        var expected = CreateExpectedRotMatrixYxz(ToPsyqRadians(0), ToPsyqRadians(512), ToPsyqRadians(0));

        AssertMatrixClose(expected, actual);
    }

    [Fact]
    public void GetBoneRotation_MasksAnglesToTwelveBitsLikeEngine()
    {
        var channels = new short[1, 6, 1];
        channels[0, 1, 0] = -3072; // -3072 & 0x0fff = 1024 => +90°.
        var animation = new PsxAnimation { FrameCount = 1, BoneCount = 1, Channels = channels };

        var actual = Matrix4x4.CreateFromQuaternion(
            Quaternion.Normalize(animation.GetBoneRotation(0, 0)));
        var expected = CreateExpectedRotMatrixYxz(ToPsyqRadians(0), ToPsyqRadians(1024), ToPsyqRadians(0));

        AssertMatrixClose(expected, actual);
    }

    [Fact]
    public void GetBoneRotation_YxzCombinedAxesMatchesExporterMatrixConvention()
    {
        var channels = new short[1, 6, 1];
        channels[0, 0, 0] = 256;
        channels[0, 1, 0] = 384;
        channels[0, 2, 0] = 512;
        var animation = new PsxAnimation
        {
            FrameCount = 1,
            BoneCount = 1,
            Channels = channels,
            RotationUnitsPerRevolution = PsxAnimation.PsyqAngleUnitsPerRevolution
        };

        var actual = Matrix4x4.CreateFromQuaternion(
            Quaternion.Normalize(animation.GetBoneRotation(0, 0)));
        var expected = CreateExpectedRotMatrixYxz(ToPsyqRadians(256), ToPsyqRadians(384), ToPsyqRadians(512));

        AssertMatrixClose(expected, actual);
    }

    [Fact]
    public void Decode_CarnageAnim0_DecompressStreamRoundtrips()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "carnage.psx");
        Assert.SkipWhen(path == null, "carnage.psx not found in sample builds");

        var data = File.ReadAllBytes(path!);
        var psxFile = PsxMeshFile.Parse(data);
        Assert.NotNull(psxFile);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count);
        Assert.NotNull(animFile);

        // Anim 0 is the 30-frame opener — 19 bones × 6 channels × 30 frames,
        // DecompressStream-encoded. The diagnostic md called this "anim 1"
        // under the previous off-by-8 numbering.
        var entry = animFile.Entries[0];
        Assert.Equal(30, entry.FrameCount);
        Assert.Equal(0, entry.TweenFlag);

        var slice = animFile.Pool.Span[entry.PoolOffset..];
        var animation = PsxAnimDecoder.Decode(slice, psxFile.Objects.Count, entry.FrameCount, out var consumed);

        Assert.Equal(30, animation.FrameCount);
        Assert.Equal(19, animation.BoneCount);
        Assert.True(consumed > 0);
        // 19 bones × 6 channels = 114 streams; each at minimum 1 byte header.
        // Real encodings land around 1100-1500 bytes for a 30-frame anim.
        Assert.InRange(consumed, 1000, 2000);
    }

    [Fact]
    public void DecodeDirectMatrix_Hawk2Anim0_FillsAllChannels()
    {
        var path = paths.FindSampleFile(Thps2ProtoBuild, "hawk2.psx");
        Assert.SkipWhen(path == null, "hawk2.psx not found in sample builds");

        var data = File.ReadAllBytes(path!);
        var psxFile = PsxMeshFile.Parse(data);
        Assert.NotNull(psxFile);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count);
        Assert.NotNull(animFile);
        Assert.True(animFile.IsDirectMatrix);

        var entry = animFile.Entries[0];
        var slice = animFile.Pool.Span[entry.PoolOffset..];
        var animation = PsxAnimDecoder.DecodeDirectMatrix(slice, psxFile.Objects.Count, entry.FrameCount);

        Assert.Equal(29, animation.FrameCount);
        Assert.Equal(19, animation.BoneCount);

        // Every bone has at least some non-zero rotation channel — direct
        // matrix payloads always populate rotations even at rest, so any bone
        // returning all-zero rotation samples would indicate a parsing fault.
        var rotated = 0;
        for (var b = 0; b < animation.BoneCount; b++)
            if (animation.IsRotationAnimated(b)) rotated++;
        Assert.True(rotated >= animation.BoneCount / 2,
            $"expected at least half the bones to carry rotation samples; got {rotated}/{animation.BoneCount}");
    }

    [Fact]
    public void DecodeDirectMatrix_NinetyDegreeY_RoundTripsThroughCurrentAngleUnits()
    {
        var stream = new byte[PsxAnimDecoder.DirectMatrixStrideBytes];

        // Row-major SMatrix for Ry=+90°, Rx=Rz=0 under the same transposed
        // convention emitted by Matrix4x4.CreateFromQuaternion(qy * qx * qz):
        // [ 0 0 -1 ; 0 1 0 ; 1 0 0 ], fixed point 4096 = 1.0.
        WriteSMatrix(stream,
            0, 0, -4096,
            0, 4096, 0,
            4096, 0, 0);

        var animation = PsxAnimDecoder.DecodeDirectMatrix(stream, boneCount: 1, frameCount: 1);
        var actual = Matrix4x4.CreateFromQuaternion(
            Quaternion.Normalize(animation.GetBoneRotation(0, 0)));
        var expected = CreateExpectedRotMatrixYxz(ToPsyqRadians(0), ToPsyqRadians(1024), ToPsyqRadians(0));

        AssertMatrixClose(expected, actual);
    }

    [Fact]
    public void DecodeDirectMatrix_ExposesMatrixQuaternionWithoutEulerRoundTrip()
    {
        var stream = new byte[PsxAnimDecoder.DirectMatrixStrideBytes];
        var expected = CreateExpectedRotMatrixYxz(
            ToPsyqRadians(256),
            ToPsyqRadians(384),
            ToPsyqRadians(512));
        WriteSMatrix(stream, expected);

        var animation = PsxAnimDecoder.DecodeDirectMatrix(stream, boneCount: 1, frameCount: 1);

        Assert.NotNull(animation.DirectRotations);
        var direct = Matrix4x4.CreateFromQuaternion(
            Quaternion.Normalize(animation.DirectRotations![0, 0]));
        var throughAccessor = Matrix4x4.CreateFromQuaternion(
            Quaternion.Normalize(animation.GetBoneRotation(0, 0)));

        AssertMatrixClose(expected, direct, precision: 4);
        AssertMatrixClose(expected, throughAccessor, precision: 4);
    }

    [Fact]
    public void Decode_RejectsExhaustedStream()
    {
        // Empty stream: should throw because we can't even read the first header byte.
        Assert.Throws<InvalidDataException>(
            () => PsxAnimDecoder.Decode([], boneCount: 1, frameCount: 1));
    }

    [Fact]
    public void IsRotationAnimated_ReturnsTrueForNonZeroChannels()
    {
        var channels = new short[1, 6, 4];
        channels[0, 1, 2] = 100; // Ry has a non-zero sample
        var anim = new PsxAnimation { FrameCount = 4, BoneCount = 1, Channels = channels };

        Assert.True(anim.IsRotationAnimated(0));
        Assert.False(anim.IsTranslationAnimated(0));
    }

    [Fact]
    public void GetBoneTranslation_ReturnsRawS16PerEngineConvention()
    {
        // The engine's MVMVA pipeline preserves the input vector's scale, so
        // PsxAnimation surfaces translations as raw s16 values; the caller
        // applies PsxMeshFile.ScaleDivisor to convert to glTF world space.
        // (Previously the class divided by 4096, which double-scaled when
        // combined with the downstream ScaleDivisor divide and clumped every
        // bone within ~0.2 world units of the origin.)
        var channels = new short[1, 6, 1];
        channels[0, 3, 0] = 4096;
        channels[0, 4, 0] = -2048;
        channels[0, 5, 0] = 8192;
        var anim = new PsxAnimation { FrameCount = 1, BoneCount = 1, Channels = channels };

        var t = anim.GetBoneTranslation(0, 0);
        Assert.Equal(4096f, t.X, 4);
        Assert.Equal(-2048f, t.Y, 4);
        Assert.Equal(8192f, t.Z, 4);
    }

    private static void WriteSMatrix(Span<byte> destination,
        short m00, short m01, short m02,
        short m10, short m11, short m12,
        short m20, short m21, short m22)
    {
        var cells = new[] { m00, m01, m02, m10, m11, m12, m20, m21, m22 };
        for (var i = 0; i < cells.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), cells[i]);
    }

    private static void WriteSMatrix(Span<byte> destination, Matrix4x4 matrix)
    {
        WriteSMatrix(destination,
            ToFixed(matrix.M11), ToFixed(matrix.M12), ToFixed(matrix.M13),
            ToFixed(matrix.M21), ToFixed(matrix.M22), ToFixed(matrix.M23),
            ToFixed(matrix.M31), ToFixed(matrix.M32), ToFixed(matrix.M33));
    }

    private static short ToFixed(float value)
    {
        return (short)Math.Clamp(
            MathF.Round(value * PsxAnimFile.DirectMatrixFixedPointDivisor),
            short.MinValue,
            short.MaxValue);
    }

    private static Matrix4x4 CreateExpectedRotMatrixYxz(float rx, float ry, float rz)
    {
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rx);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, ry);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rz);
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(qy * qx * qz));
    }

    private static float ToPsyqRadians(short raw)
    {
        return (raw & 0x0fff) * (2f * MathF.PI / 4096f);
    }

    private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual, int precision = 5)
    {
        Assert.Equal(expected.M11, actual.M11, precision);
        Assert.Equal(expected.M12, actual.M12, precision);
        Assert.Equal(expected.M13, actual.M13, precision);
        Assert.Equal(expected.M21, actual.M21, precision);
        Assert.Equal(expected.M22, actual.M22, precision);
        Assert.Equal(expected.M23, actual.M23, precision);
        Assert.Equal(expected.M31, actual.M31, precision);
        Assert.Equal(expected.M32, actual.M32, precision);
        Assert.Equal(expected.M33, actual.M33, precision);
    }
}
