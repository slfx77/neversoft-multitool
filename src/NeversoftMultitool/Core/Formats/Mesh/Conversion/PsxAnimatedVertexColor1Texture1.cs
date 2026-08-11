using System.Numerics;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     glTF vertex payload for PS1 texture-wibble geometry. Core glTF receives
///     normalized colour and frame-zero UV fallbacks; NeversoftMultitool's live
///     viewer consumes the custom attributes to reproduce per-vertex UV motion.
/// </summary>
internal struct PsxAnimatedVertexColor1Texture1 :
    IVertexCustom,
    IEquatable<PsxAnimatedVertexColor1Texture1>
{
    internal const string ColorAttributeName = "_PSX_COLOR_0";
    internal const string FlagsAttributeName = PsxGltfVertexCarriers.FlagsAndPulseAttributeName;
    internal const string MotionAttributeName = PsxGltfVertexCarriers.WibbleVelocityAttributeName;
    internal const string WaveAttributeName = PsxGltfVertexCarriers.WibbleWaveAttributeName;
    internal const string SizeAttributeName = PsxGltfVertexCarriers.WibbleSizeAttributeName;

    private static readonly KeyValuePair<string, AttributeFormat>[] EncodingAttributes =
    [
        new("COLOR_0", new AttributeFormat(
            DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, true)),
        new("TEXCOORD_0", new AttributeFormat(DimensionType.VEC2)),
        new(ColorAttributeName, new AttributeFormat(DimensionType.VEC4)),
        new(FlagsAttributeName, new AttributeFormat(
            DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, true)),
        new(MotionAttributeName, new AttributeFormat(DimensionType.VEC2)),
        new(WaveAttributeName, new AttributeFormat(DimensionType.VEC2)),
        new(SizeAttributeName, new AttributeFormat(DimensionType.VEC2))
    ];

    private static readonly string[] CustomAttributeNames =
    [
        ColorAttributeName
    ];

    internal PsxAnimatedVertexColor1Texture1(ModelVertex vertex)
    {
        PortableColor = Vector4.Clamp(vertex.Color, Vector4.Zero, Vector4.One);
        PsxColor = vertex.PsxPacketColor ?? vertex.Color;
        PsxFlagsAndPulse = PsxGltfVertexCarriers.EncodeFlagsAndPulse(
            vertex.PsxPrimitiveFlags,
            vertex.ColourPulseChannel);
        TexCoord = vertex.TexCoord;

        if (vertex.TextureWibble is { } wibble)
        {
            if (wibble.TextureWidth <= 0 || wibble.TextureHeight <= 0)
            {
                throw new InvalidOperationException(
                    "PS1 texture-wibble dimensions must be positive; zero is reserved for the no-wibble sentinel.");
            }

            WibbleVelocity = new Vector2(
                wibble.UVelocity,
                PsxGltfVertexCarriers.EncodeSecondTexCoordComponent(wibble.VVelocity));
            WibbleWave = new Vector2(
                wibble.Frequency,
                PsxGltfVertexCarriers.EncodeSecondTexCoordComponent(
                    PsxGltfVertexCarriers.PackWibbleNibbles(
                        wibble.UAmplitude,
                        wibble.UPhase,
                        wibble.VAmplitude,
                        wibble.VPhase)));
            TextureSize = new Vector2(
                wibble.TextureWidth,
                PsxGltfVertexCarriers.EncodeSecondTexCoordComponent(wibble.TextureHeight));
        }
        else
        {
            WibbleVelocity = new Vector2(0f, 1f);
            WibbleWave = new Vector2(0f, 1f);
            // Real wibbles always have positive dimensions (FromFace clamps
            // each to at least one), making logical (0,0) unambiguous.
            TextureSize = new Vector2(0f, 1f);
        }
    }

    public Vector4 PortableColor;
    public Vector4 PsxColor;
    public Vector4 PsxFlagsAndPulse;
    public Vector2 TexCoord;
    public Vector2 WibbleVelocity;
    public Vector2 WibbleWave;
    public Vector2 TextureSize;

    public readonly int MaxColors => 2;
    public readonly int MaxTextCoords => 4;
    public readonly IEnumerable<string> CustomAttributes => CustomAttributeNames;

    readonly IEnumerable<KeyValuePair<string, AttributeFormat>>
        IVertexReflection.GetEncodingAttributes()
    {
        return EncodingAttributes;
    }

    public readonly Vector4 GetColor(int index)
    {
        return index switch
        {
            0 => PortableColor,
            1 => PsxFlagsAndPulse,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    public readonly Vector2 GetTexCoord(int index)
    {
        return index switch
        {
            0 => TexCoord,
            1 => WibbleVelocity,
            2 => WibbleWave,
            3 => TextureSize,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    public void SetColor(int setIndex, Vector4 color)
    {
        switch (setIndex)
        {
            case 0:
                PortableColor = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
                break;
            case 1:
                PsxFlagsAndPulse = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setIndex));
        }
    }

    public void SetTexCoord(int setIndex, Vector2 coord)
    {
        switch (setIndex)
        {
            case 0:
                TexCoord = coord;
                break;
            case 1:
                WibbleVelocity = coord;
                break;
            case 2:
                WibbleWave = coord;
                break;
            case 3:
                TextureSize = coord;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setIndex));
        }
    }

    public readonly bool TryGetCustomAttribute(string attributeName, out object? value)
    {
        value = attributeName switch
        {
            ColorAttributeName => PsxColor,
            _ => null
        };
        return value != null;
    }

    public void SetCustomAttribute(string attributeName, object value)
    {
        switch (attributeName, value)
        {
            case (ColorAttributeName, Vector4 color):
                PsxColor = color;
                break;
        }
    }

    public readonly void Validate()
    {
        if (!IsFinite(PortableColor) || !IsFinite(PsxColor) || !IsFinite(PsxFlagsAndPulse) ||
            !IsFinite(TexCoord) || !IsFinite(WibbleVelocity) ||
            !IsFinite(WibbleWave) || !IsFinite(TextureSize))
        {
            throw new InvalidOperationException(
                "PS1 animated vertex attributes must be finite.");
        }
    }

    public readonly VertexMaterialDelta Subtract(IVertexMaterial baseValue)
    {
        ArgumentNullException.ThrowIfNull(baseValue);
        return new VertexMaterialDelta(
            PortableColor - baseValue.GetColor(0),
            Vector4.Zero,
            TexCoord - baseValue.GetTexCoord(0),
            Vector2.Zero);
    }

#pragma warning disable RCS1242 // SharpGLTF's IVertexMaterial contract requires an in parameter.
    public void Add(in VertexMaterialDelta delta)
    {
        PortableColor = Vector4.Clamp(
            PortableColor + delta.Color0Delta,
            Vector4.Zero,
            Vector4.One);
        TexCoord += delta.TexCoord0Delta;
    }
#pragma warning restore RCS1242

    public readonly bool Equals(PsxAnimatedVertexColor1Texture1 other)
    {
        return PortableColor.Equals(other.PortableColor)
               && PsxColor.Equals(other.PsxColor)
               && PsxFlagsAndPulse.Equals(other.PsxFlagsAndPulse)
               && TexCoord.Equals(other.TexCoord)
               && WibbleVelocity.Equals(other.WibbleVelocity)
               && WibbleWave.Equals(other.WibbleWave)
               && TextureSize.Equals(other.TextureSize);
    }

    public readonly override bool Equals(object? obj)
    {
        return obj is PsxAnimatedVertexColor1Texture1 other && Equals(other);
    }

    public readonly override int GetHashCode()
    {
        var first = HashCode.Combine(
            PortableColor, PsxColor, PsxFlagsAndPulse, TexCoord, WibbleVelocity);
        return HashCode.Combine(first, WibbleWave, TextureSize);
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    private static bool IsFinite(Vector4 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
