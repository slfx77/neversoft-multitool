using System.Numerics;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
    private static ModelVertex MakeCollisionVertex(ColObject obj, int index)
    {
        var intensity = index < obj.Intensities.Length ? obj.Intensities[index] / 255f : 1f;
        return new ModelVertex(
            obj.Vertices[index],
            Vector3.UnitY,
            new Vector4(intensity, intensity, intensity, 1f),
            Vector2.Zero);
    }

    private static ModelVertex MakeDdmVertex(DdmVertex vertex, float normalOffset = 0f)
    {
        var normal = NormalizeOrDefault(new Vector3(-vertex.NX, -vertex.NY, vertex.NZ));
        var position = new Vector3(-vertex.X, -vertex.Y, vertex.Z);
        if (normalOffset > 0f)
            position += normal * normalOffset;

        return new ModelVertex(
            position,
            normal,
            new Vector4(vertex.R / 255f, vertex.G / 255f, vertex.B / 255f, vertex.A / 255f),
            new Vector2(vertex.U, vertex.V));
    }

    private static ModelVertex MakePsxVertex(
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        int slot,
        Vector4 color,
        (int Width, int Height) texDims)
    {
        var vertexIndex = GetPsxFaceVertexIndex(face, slot);
        if (vertexIndex >= mesh.Vertices.Count)
            return new ModelVertex(Vector3.Zero, Vector3.UnitY, color, Vector2.Zero);

        var nativeVertex = mesh.Vertices[(int)vertexIndex];
        var texCoord = face.GetTextureCoordinate(slot);
        return new ModelVertex(
            new Vector3(nativeVertex.X, -nativeVertex.Y, -nativeVertex.Z),
            ComputePsxVertexNormal(mesh, face, vertexIndex),
            color,
            ComputePsxTextureUv(version, face, texCoord.U, texCoord.V, texDims.Width, texDims.Height));
    }

    private static ModelVertex MakePs2Vertex(
        Ps2Vertex vertex,
        bool preserveVertexAlpha,
        bool bakeVertexColorsToWhite,
        Vector3? fallbackNormal = null)
    {
        var r = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.R / 128f, 1f);
        var g = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.G / 128f, 1f);
        var b = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.B / 128f, 1f);
        var a = bakeVertexColorsToWhite ? 1f : preserveVertexAlpha ? Math.Min(vertex.A / 128f, 1f) : 1f;

        // Worldzone leaves rarely carry per-vertex normals in their source VIF
        // batches — `vertex.HasNormal == false` is common — so callers may pass
        // a flat face normal as a fallback for optional lighting and viewer
        // normals. Source vertex colours do not depend on this fallback.
        var hasUsableNormal = vertex.HasNormal || fallbackNormal.HasValue;
        var normal = vertex.HasNormal
            ? NormalizeOrDefault(vertex.Normal)
            : fallbackNormal ?? Vector3.UnitY;

        if (!bakeVertexColorsToWhite && _activePs2WorldzoneLighting is { } lighting && hasUsableNormal)
        {
            var diffuse = MathF.Max(0f, Vector3.Dot(normal, lighting.SunDirection));
            var rf = lighting.Ambient.X + diffuse * lighting.SunColor.X;
            var gf = lighting.Ambient.Y + diffuse * lighting.SunColor.Y;
            var bf = lighting.Ambient.Z + diffuse * lighting.SunColor.Z;
            r = Math.Min(r * rf, 1f);
            g = Math.Min(g * gf, 1f);
            b = Math.Min(b * bf, 1f);
        }

        return new ModelVertex(
            vertex.Position,
            normal,
            new Vector4(r, g, b, a),
            vertex.HasUV ? new Vector2(vertex.U, 1f - vertex.V) : Vector2.Zero);
    }

    private static ModelVertex MakeXbxVertex(XbxVertex vertex, float coordinateScale)
    {
        var color = vertex.HasColor
            ? new Vector4(
                Math.Min(vertex.Color.X, 1f),
                Math.Min(vertex.Color.Y, 1f),
                Math.Min(vertex.Color.Z, 1f),
                Math.Min(vertex.Color.W, 1f))
            : Vector4.One;

        return new ModelVertex(
            vertex.Position * coordinateScale,
            vertex.HasNormal ? NormalizeOrDefault(vertex.Normal) : Vector3.UnitY,
            color,
            vertex.TexCoord);
    }

    private static ModelVertex MakeRwVertex(RwGeometry geometry, int index)
    {
        var position = index < geometry.Vertices.Length ? geometry.Vertices[index] : Vector3.Zero;
        var normal = geometry.Normals != null && index < geometry.Normals.Length
            ? NormalizeOrDefault(geometry.Normals[index])
            : Vector3.UnitY;
        var color = geometry.Colors != null && index < geometry.Colors.Length
            ? ToColor(geometry.Colors[index])
            : Vector4.One;
        var uv = geometry.UVs != null && index < geometry.UVs.Length ? geometry.UVs[index] : Vector2.Zero;
        return new ModelVertex(position, normal, color, uv);
    }

    private static ModelVertex MakeRwBspVertex(RwBspSection section, int index)
    {
        var position = index < section.Vertices.Length ? section.Vertices[index] : Vector3.Zero;
        var normal = section.Normals != null && index < section.Normals.Length
            ? NormalizeOrDefault(section.Normals[index])
            : Vector3.UnitY;
        var color = section.Colors != null && index < section.Colors.Length
            ? ToColor(section.Colors[index])
            : Vector4.One;
        var uv = section.UVs != null && index < section.UVs.Length ? section.UVs[index] : Vector2.Zero;
        return new ModelVertex(position, normal, color, uv);
    }

}
