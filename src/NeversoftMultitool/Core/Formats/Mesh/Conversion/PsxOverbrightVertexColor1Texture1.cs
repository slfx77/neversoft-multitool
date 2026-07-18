using System.Numerics;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Transports a PS1 GPU colour multiplier without violating the normalized
///     range required by glTF's standard <c>COLOR_0</c> semantic. Portable glTF
///     consumers receive a clamped, normalized fallback; NeversoftMultitool's
///     renderers prefer the original floating-point custom attribute.
/// </summary>
internal struct PsxOverbrightVertexColor1Texture1 :
    IVertexCustom,
    IEquatable<PsxOverbrightVertexColor1Texture1>
{
    internal const string AttributeName = "_PSX_COLOR_0";

    private static readonly KeyValuePair<string, AttributeFormat>[] EncodingAttributes =
    [
        new("COLOR_0", new AttributeFormat(
            DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, true)),
        new("TEXCOORD_0", new AttributeFormat(DimensionType.VEC2)),
        new(AttributeName, new AttributeFormat(
            DimensionType.VEC4, EncodingType.FLOAT, false))
    ];

    private static readonly string[] CustomAttributeNames = [AttributeName];

    internal PsxOverbrightVertexColor1Texture1(Vector4 color, Vector2 texCoord)
    {
        PortableColor = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
        PsxColor = color;
        TexCoord = texCoord;
    }

    public Vector4 PortableColor;
    public Vector4 PsxColor;
    public Vector2 TexCoord;

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
        if (attributeName == AttributeName)
        {
            value = PsxColor;
            return true;
        }

        value = null;
        return false;
    }

    public void SetCustomAttribute(string attributeName, object value)
    {
        if (attributeName == AttributeName && value is Vector4 color)
            PsxColor = color;
    }

    public readonly void Validate()
    {
        if (!IsFinite(PortableColor.X) || !IsFinite(PortableColor.Y) ||
            !IsFinite(PortableColor.Z) || !IsFinite(PortableColor.W) ||
            !IsFinite(PsxColor.X) || !IsFinite(PsxColor.Y) ||
            !IsFinite(PsxColor.Z) || !IsFinite(PsxColor.W) ||
            !IsFinite(TexCoord.X) || !IsFinite(TexCoord.Y))
        {
            throw new InvalidOperationException(
                "Vertex colour multipliers and texture coordinates must be finite.");
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

    public readonly bool Equals(PsxOverbrightVertexColor1Texture1 other)
    {
        return PortableColor.Equals(other.PortableColor)
               && PsxColor.Equals(other.PsxColor)
               && TexCoord.Equals(other.TexCoord);
    }

    public override readonly bool Equals(object? obj)
    {
        return obj is PsxOverbrightVertexColor1Texture1 other && Equals(other);
    }

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(PortableColor, PsxColor, TexCoord);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
