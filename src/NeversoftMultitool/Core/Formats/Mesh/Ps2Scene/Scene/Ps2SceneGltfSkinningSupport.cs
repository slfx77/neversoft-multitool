using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

internal static class Ps2SceneGltfSkinningSupport
{
    internal static int AddSkinnedTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexJoints4> prim,
        Ps2Vertex[] verts,
        bool startsOnOddOutputSlot = false)
    {
        var count = 0;
        var stripStart = 0;
        var parityBias = startsOnOddOutputSlot ? 1 : 0;

        for (var i = 0; i < verts.Length; i++)
        {
            if (verts[i].IsStripRestart)
                continue;

            var localIndex = i - stripStart;
            if (localIndex < 2)
                continue;

            VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4> va, vb, vc;
            Ps2Vertex pa, pb, pc;
            if (((localIndex + parityBias) & 1) == 0)
            {
                pa = verts[i - 2];
                pb = verts[i - 1];
                pc = verts[i];
                va = MakeSkinnedVertex(verts[i - 2]);
                vb = MakeSkinnedVertex(verts[i - 1]);
                vc = MakeSkinnedVertex(verts[i]);
            }
            else
            {
                pa = verts[i - 1];
                pb = verts[i - 2];
                pc = verts[i];
                va = MakeSkinnedVertex(verts[i - 1]);
                vb = MakeSkinnedVertex(verts[i - 2]);
                vc = MakeSkinnedVertex(verts[i]);
            }

            if (Ps2SceneGltfWriter.IsDegenerate(pa, pb, pc))
                continue;

            prim.AddTriangle(va, vb, vc);
            count++;
        }

        return count;
    }

    private static VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4> MakeSkinnedVertex(
        in Ps2Vertex vertex)
    {
        var position = vertex.Position;
        var normal = Vector3.UnitY;
        if (vertex.HasNormal)
        {
            var length = vertex.Normal.Length();
            normal = length > 0.001f ? vertex.Normal / length : Vector3.UnitY;
        }

        var r = Math.Min(vertex.R / 128f, 1f);
        var g = Math.Min(vertex.G / 128f, 1f);
        var b = Math.Min(vertex.B / 128f, 1f);
        var a = Math.Min(vertex.A / 128f, 1f);
        var uv = vertex.HasUV ? new Vector2(vertex.U, 1f - vertex.V) : Vector2.Zero;

        var skinning = vertex.HasSkinData
            ? new VertexJoints4(
                (vertex.BoneIndex0, vertex.BoneWeight0),
                (vertex.BoneIndex1, vertex.BoneWeight1),
                (vertex.BoneIndex2, vertex.BoneWeight2))
            : new VertexJoints4((0, 1f));

        return new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>(
            new VertexPositionNormal(position, normal),
            new VertexColor1Texture1(new Vector4(r, g, b, a), uv),
            skinning);
    }
}
