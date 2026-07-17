using System.Numerics;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     SharpGLTF's stock color/UV fragment stores <c>COLOR_0</c> as normalized
///     bytes. That is adequate for source byte colours, but not after an sRGB
///     transfer function maps dark display values into a tightly packed linear
///     range. Normalized 16-bit colour preserves those values without the size
///     cost of four floating-point components; texture coordinates remain
///     floating point as usual.
/// </summary>
internal struct HighPrecisionVertexColor1Texture1 :
    IVertexMaterial,
    IEquatable<HighPrecisionVertexColor1Texture1>
{
    private static readonly KeyValuePair<string, AttributeFormat>[] EncodingAttributes =
    [
        new("COLOR_0", new AttributeFormat(
            DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, true)),
        new("TEXCOORD_0", new AttributeFormat(DimensionType.VEC2))
    ];

    internal HighPrecisionVertexColor1Texture1(Vector4 color, Vector2 texCoord)
    {
        Color = color;
        TexCoord = texCoord;
    }

    public Vector4 Color;
    public Vector2 TexCoord;

    public readonly int MaxColors => 1;
    public readonly int MaxTextCoords => 1;

    readonly IEnumerable<KeyValuePair<string, AttributeFormat>>
        IVertexReflection.GetEncodingAttributes() => EncodingAttributes;

    public readonly Vector4 GetColor(int index)
    {
        return index == 0
            ? Color
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
        Color = color;
    }

    public void SetTexCoord(int setIndex, Vector2 coord)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(setIndex, 0);
        TexCoord = coord;
    }

    public readonly VertexMaterialDelta Subtract(IVertexMaterial baseValue)
    {
        ArgumentNullException.ThrowIfNull(baseValue);
        var colorDelta = Color - baseValue.GetColor(0);
        var emptyColorDelta = Vector4.Zero;
        var texCoordDelta = TexCoord - baseValue.GetTexCoord(0);
        var emptyTexCoordDelta = Vector2.Zero;
        return new VertexMaterialDelta(
            colorDelta, emptyColorDelta, texCoordDelta, emptyTexCoordDelta);
    }

#pragma warning disable RCS1242 // SharpGLTF's IVertexMaterial contract requires an in parameter.
    public void Add(in VertexMaterialDelta delta)
    {
        Color += delta.Color0Delta;
        TexCoord += delta.TexCoord0Delta;
    }
#pragma warning restore RCS1242

    public readonly bool Equals(HighPrecisionVertexColor1Texture1 other)
    {
        return Color.Equals(other.Color) && TexCoord.Equals(other.TexCoord);
    }

    public override readonly bool Equals(object? obj)
    {
        return obj is HighPrecisionVertexColor1Texture1 other && Equals(other);
    }

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(Color, TexCoord);
    }
}
