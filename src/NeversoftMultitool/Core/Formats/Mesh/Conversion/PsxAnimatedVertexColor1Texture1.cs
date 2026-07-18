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
    internal const string MotionAttributeName = "_PSX_UV_WIBBLE_0";
    internal const string WaveAttributeName = "_PSX_UV_WIBBLE_1";
    internal const string SizeAttributeName = "_PSX_UV_WIBBLE_2";

    private static readonly KeyValuePair<string, AttributeFormat>[] EncodingAttributes =
    [
        new("COLOR_0", new AttributeFormat(
            DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, true)),
        new("TEXCOORD_0", new AttributeFormat(DimensionType.VEC2)),
        new(ColorAttributeName, new AttributeFormat(DimensionType.VEC4)),
        new(MotionAttributeName, new AttributeFormat(DimensionType.VEC4)),
        new(WaveAttributeName, new AttributeFormat(DimensionType.VEC4)),
        new(SizeAttributeName, new AttributeFormat(DimensionType.VEC2))
    ];

    private static readonly string[] CustomAttributeNames =
    [
        ColorAttributeName,
        MotionAttributeName,
        WaveAttributeName,
        SizeAttributeName
    ];

    internal PsxAnimatedVertexColor1Texture1(ModelVertex vertex)
    {
        PortableColor = Vector4.Clamp(vertex.Color, Vector4.Zero, Vector4.One);
        PsxColor = vertex.Color;
        TexCoord = vertex.TexCoord;

        if (vertex.TextureWibble is { } wibble)
        {
            Motion = new Vector4(
                wibble.UVelocity,
                wibble.VVelocity,
                wibble.Frequency,
                1f);
            Wave = new Vector4(
                wibble.UAmplitude,
                wibble.UPhase,
                wibble.VAmplitude,
                wibble.VPhase);
            TextureSize = new Vector2(wibble.TextureWidth, wibble.TextureHeight);
        }
        else
        {
            Motion = Vector4.Zero;
            Wave = Vector4.Zero;
            TextureSize = Vector2.One;
        }
    }

    public Vector4 PortableColor;
    public Vector4 PsxColor;
    public Vector2 TexCoord;
    public Vector4 Motion;
    public Vector4 Wave;
    public Vector2 TextureSize;

    public readonly int MaxColors => 1;
    public readonly int MaxTextCoords => 1;
    public readonly IEnumerable<string> CustomAttributes => CustomAttributeNames;

    readonly IEnumerable<KeyValuePair<string, AttributeFormat>>
        IVertexReflection.GetEncodingAttributes() => EncodingAttributes;

    public readonly Vector4 GetColor(int index)
    {
        return index == 0
            ? PortableColor
            : throw new ArgumentOutOfRangeException(nameof(index));
    }

    public readonly Vector2 GetTexCoord(int index)
    {
        return index == 0
            ? TexCoord
            : throw new ArgumentOutOfRangeException(nameof(index));
    }

    public void SetColor(int setIndex, Vector4 color)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(setIndex, 0);
        PortableColor = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
    }

    public void SetTexCoord(int setIndex, Vector2 coord)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(setIndex, 0);
        TexCoord = coord;
    }

    public readonly bool TryGetCustomAttribute(string attributeName, out object? value)
    {
        value = attributeName switch
        {
            ColorAttributeName => PsxColor,
            MotionAttributeName => Motion,
            WaveAttributeName => Wave,
            SizeAttributeName => TextureSize,
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
            case (MotionAttributeName, Vector4 motion):
                Motion = motion;
                break;
            case (WaveAttributeName, Vector4 wave):
                Wave = wave;
                break;
            case (SizeAttributeName, Vector2 size):
                TextureSize = size;
                break;
        }
    }

    public readonly void Validate()
    {
        if (!IsFinite(PortableColor) || !IsFinite(PsxColor) ||
            !IsFinite(TexCoord) || !IsFinite(Motion) ||
            !IsFinite(Wave) || !IsFinite(TextureSize))
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
               && TexCoord.Equals(other.TexCoord)
               && Motion.Equals(other.Motion)
               && Wave.Equals(other.Wave)
               && TextureSize.Equals(other.TextureSize);
    }

    public override readonly bool Equals(object? obj)
    {
        return obj is PsxAnimatedVertexColor1Texture1 other && Equals(other);
    }

    public override readonly int GetHashCode()
    {
        var first = HashCode.Combine(PortableColor, PsxColor, TexCoord, Motion);
        return HashCode.Combine(first, Wave, TextureSize);
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
