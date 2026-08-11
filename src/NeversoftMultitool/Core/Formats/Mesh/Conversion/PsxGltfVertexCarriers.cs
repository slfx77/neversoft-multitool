using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Defines the Blender-safe standard glTF carriers used beside the sole
///     PS1 custom vertex semantic, <c>_PSX_COLOR_0</c>.
/// </summary>
internal static class PsxGltfVertexCarriers
{
    internal const string FlagsAndPulseAttributeName = "COLOR_1";
    internal const string WibbleVelocityAttributeName = "TEXCOORD_1";
    internal const string WibbleWaveAttributeName = "TEXCOORD_2";
    internal const string WibbleSizeAttributeName = "TEXCOORD_3";

    private const float ByteMaximum = byte.MaxValue;

    /// <summary>
    ///     COLOR_1 is normalized unsigned-short RGBA. RGB retain the three
    ///     independent binary primitive flags; A stores the 1-based pulse-table
    ///     channel as an exact 8-bit code point. Blender imports every COLOR_n
    ///     as BYTE_COLOR, even from a ushort accessor, so using the 256 exact
    ///     values n/255 is what makes the lane survive that conversion. Alpha
    ///     is not colour-space transformed. A zero channel means static colour.
    /// </summary>
    internal static Vector4 EncodeFlagsAndPulse(Vector3 flags, int oneBasedPulseChannel)
    {
        if ((uint)oneBasedPulseChannel > byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"PS1 colour-pulse channel {oneBasedPulseChannel} exceeds the COLOR_1 byte codebook.");
        }

        return new Vector4(
            flags.X,
            flags.Y,
            flags.Z,
            PsxColourPulseLane.Encode(oneBasedPulseChannel));
    }

    internal static int DecodeOneBasedPulseChannel(float normalizedValue)
    {
        return Math.Clamp((int)MathF.Round(normalizedValue * ByteMaximum), 0, byte.MaxValue);
    }

    internal static int DecodePulseTableIndex(float normalizedValue)
    {
        return DecodeOneBasedPulseChannel(normalizedValue) - 1;
    }

    /// <summary>
    ///     Packs U amplitude, U phase, V amplitude, and V phase, in that order,
    ///     into one exactly representable 16-bit integer stored as a float.
    /// </summary>
    internal static ushort PackWibbleNibbles(
        byte uAmplitude,
        byte uPhase,
        byte vAmplitude,
        byte vPhase)
    {
        ValidateNibble(uAmplitude, nameof(uAmplitude));
        ValidateNibble(uPhase, nameof(uPhase));
        ValidateNibble(vAmplitude, nameof(vAmplitude));
        ValidateNibble(vPhase, nameof(vPhase));

        return (ushort)((uAmplitude << 12) | (uPhase << 8) |
                        (vAmplitude << 4) | vPhase);
    }

    internal static (byte UAmplitude, byte UPhase, byte VAmplitude, byte VPhase)
        UnpackWibbleNibbles(float packedValue)
    {
        var packed = (ushort)Math.Clamp(
            (int)MathF.Round(packedValue),
            ushort.MinValue,
            ushort.MaxValue);
        return (
            (byte)((packed >> 12) & 0x0F),
            (byte)((packed >> 8) & 0x0F),
            (byte)((packed >> 4) & 0x0F),
            (byte)(packed & 0x0F));
    }

    /// <summary>
    ///     Blender's glTF importer applies <c>1-v</c> to every TEXCOORD set,
    ///     including application data. Pre-flipping each carrier's second lane
    ///     makes Blender expose the authored value. Raw-glTF consumers reverse
    ///     this operation with <see cref="DecodeSecondTexCoordComponent" />.
    /// </summary>
    internal static float EncodeSecondTexCoordComponent(float logicalValue)
    {
        return 1f - logicalValue;
    }

    internal static float DecodeSecondTexCoordComponent(float encodedValue)
    {
        return 1f - encodedValue;
    }

    internal static ModelTextureWibble? DecodeTextureWibble(
        Vector2 velocityCarrier,
        Vector2 waveCarrier,
        Vector2 sizeCarrier)
    {
        var width = (int)MathF.Round(sizeCarrier.X);
        var height = (int)MathF.Round(
            DecodeSecondTexCoordComponent(sizeCarrier.Y));
        if (width == 0 && height == 0)
            return null;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Invalid PS1 texture-wibble size carrier.");

        var packed = DecodeSecondTexCoordComponent(waveCarrier.Y);
        var (uAmplitude, uPhase, vAmplitude, vPhase) =
            UnpackWibbleNibbles(packed);
        return new ModelTextureWibble(
            (int)MathF.Round(velocityCarrier.X),
            (int)MathF.Round(DecodeSecondTexCoordComponent(velocityCarrier.Y)),
            (int)MathF.Round(waveCarrier.X),
            uAmplitude,
            uPhase,
            vAmplitude,
            vPhase,
            width,
            height);
    }

    private static void ValidateNibble(byte value, string parameterName)
    {
        if (value > 0x0F)
            throw new ArgumentOutOfRangeException(parameterName, value, "Wibble values must fit in four bits.");
    }
}
