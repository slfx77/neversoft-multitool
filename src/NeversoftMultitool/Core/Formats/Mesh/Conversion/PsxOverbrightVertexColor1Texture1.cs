using System.Numerics;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Transports raw PS1 GPU packet RGB without violating the normalized range
///     required by glTF's standard <c>COLOR_0</c> semantic. Portable glTF
///     consumers receive the linear fallback; NeversoftMultitool's live viewer
///     uses the custom attribute for native display-domain interpolation.
/// </summary>
internal struct PsxOverbrightVertexColor1Texture1 :
    IVertexCustom,
    IEquatable<PsxOverbrightVertexColor1Texture1>
{
    internal const string AttributeName = "_PSX_COLOR_0";
    internal const string FlagsAttributeName = "_PSX_FLAGS_0";

    private static readonly KeyValuePair<string, AttributeFormat>[] EncodingAttributes =
    [
        new("COLOR_0", new AttributeFormat(
            DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, true)),
        new("TEXCOORD_0", new AttributeFormat(DimensionType.VEC2)),
        new(AttributeName, new AttributeFormat(
            DimensionType.VEC4, EncodingType.FLOAT, false)),
        new(FlagsAttributeName, new AttributeFormat(
            DimensionType.VEC3, EncodingType.FLOAT, false))
    ];

    private static readonly string[] CustomAttributeNames =
        [AttributeName, FlagsAttributeName];

    internal PsxOverbrightVertexColor1Texture1(ModelVertex vertex)
    {
        PortableColor = Vector4.Clamp(vertex.Color, Vector4.Zero, Vector4.One);
        PsxColor = vertex.PsxPacketColor ?? vertex.Color;
        PsxFlags = vertex.PsxPrimitiveFlags;
        TexCoord = vertex.TexCoord;
    }

    public Vector4 PortableColor;
    public Vector4 PsxColor;
    public Vector3 PsxFlags;
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

        if (attributeName == FlagsAttributeName)
        {
            value = PsxFlags;
            return true;
        }

        value = null;
        return false;
    }

    public void SetCustomAttribute(string attributeName, object value)
    {
        switch (attributeName, value)
        {
            case (AttributeName, Vector4 color):
                PsxColor = color;
                break;
            case (FlagsAttributeName, Vector3 flags):
                PsxFlags = flags;
                break;
        }
    }

    public readonly void Validate()
    {
        if (!IsFinite(PortableColor.X) || !IsFinite(PortableColor.Y) ||
            !IsFinite(PortableColor.Z) || !IsFinite(PortableColor.W) ||
            !IsFinite(PsxColor.X) || !IsFinite(PsxColor.Y) ||
            !IsFinite(PsxColor.Z) || !IsFinite(PsxColor.W) ||
            !IsFinite(PsxFlags.X) || !IsFinite(PsxFlags.Y) ||
            !IsFinite(PsxFlags.Z) ||
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
               && PsxFlags.Equals(other.PsxFlags)
               && TexCoord.Equals(other.TexCoord);
    }

    public override readonly bool Equals(object? obj)
    {
        return obj is PsxOverbrightVertexColor1Texture1 other && Equals(other);
    }

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(PortableColor, PsxColor, PsxFlags, TexCoord);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
