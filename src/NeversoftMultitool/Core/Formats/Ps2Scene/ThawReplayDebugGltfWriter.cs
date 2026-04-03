using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats.Mesh;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

internal static class ThawReplayDebugGltfWriter
{
    public static int WriteReplayKicks(
        IReadOnlyList<ThawReplayKickExtractor.ExtractedKick> kicks,
        string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var totalTriangles = 0;

        foreach (var kick in kicks)
        {
            var kickName = GetKickName(kick);
            var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(kickName);
            var prim = mesh.UsePrimitive(
                new MaterialBuilder(kickName)
                    .WithUnlitShader()
                    .WithBaseColor(GetKickColor(kick.KickIndex))
                    .WithDoubleSide(true));

            var kickTriangles = 0;
            foreach (var kickMesh in kick.Meshes)
            {
                if (kickMesh.Vertices.Length < 3)
                    continue;

                kickTriangles += AddTriangleStrip(prim, kickMesh.Vertices, kickMesh.StartsOnOddOutputSlot);
            }

            if (kickTriangles == 0)
                continue;

            totalTriangles += kickTriangles;
            scene.AddRigidMesh(mesh, new NodeBuilder(kickName));
        }

        if (totalTriangles == 0)
            return 0;

        var model = scene.ToGltf2();
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return totalTriangles;
    }

    public static void WriteKickReport(
        IReadOnlyList<ThawReplayKickExtractor.ExtractedKick> kicks,
        string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, FormatKickReport(kicks));
    }

    internal static string FormatKickReport(IReadOnlyList<ThawReplayKickExtractor.ExtractedKick> kicks)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "KickIndex\tBatchIndex\tSetupIndex\tFirstCommandOffset\tAddress\tNloop\tFullStart\tFullEnd\tTriangles\tMeshes\tColor\tName");
        foreach (var kick in kicks)
        {
            var colorHex = GetKickColorHex(kick.KickIndex);
            sb.Append(kick.KickIndex).Append('\t')
                .Append(kick.BatchIndex).Append('\t')
                .Append(kick.SetupIndex).Append('\t')
                .Append("0x").Append(kick.FirstCommandOffset.ToString("X6")).Append('\t')
                .Append(kick.KickPacket.Address).Append('\t')
                .Append(kick.KickPacket.Nloop).Append('\t')
                .Append(kick.FullOutputWindow.Length > 0 ? kick.FullOutputWindow[0] : -1).Append('\t')
                .Append(kick.FullOutputWindow.Length > 0 ? kick.FullOutputWindow[^1] : -1).Append('\t')
                .Append(kick.TriangleCount).Append('\t')
                .Append(kick.Meshes.Length).Append('\t')
                .Append(colorHex).Append('\t')
                .Append(GetKickName(kick))
                .AppendLine();
        }

        return sb.ToString();
    }

    internal static string GetKickName(ThawReplayKickExtractor.ExtractedKick kick)
    {
        return
            $"kick_{kick.KickIndex:D3}_batch_{kick.BatchIndex:D3}_setup_{kick.SetupIndex:D2}_0x{kick.FirstCommandOffset:X6}_addr_{kick.KickPacket.Address}";
    }

    internal static string GetKickColorHex(int kickIndex)
    {
        var color = GetKickColor(kickIndex);
        var r = (int)Math.Round(color.X * 255f);
        var g = (int)Math.Round(color.Y * 255f);
        var b = (int)Math.Round(color.Z * 255f);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static Vector4 GetKickColor(int kickIndex)
    {
        var hue = (float)(kickIndex * 0.6180339887498949 % 1.0);
        var rgb = HsvToRgb(hue, 0.72f, 1.0f);
        return new Vector4(rgb, 1f);
    }

    private static Vector3 HsvToRgb(float hue, float saturation, float value)
    {
        var scaledHue = hue * 6f;
        var sector = (int)MathF.Floor(scaledHue);
        var fraction = scaledHue - sector;
        var p = value * (1f - saturation);
        var q = value * (1f - fraction * saturation);
        var t = value * (1f - (1f - fraction) * saturation);

        return sector switch
        {
            0 => new Vector3(value, t, p),
            1 => new Vector3(q, value, p),
            2 => new Vector3(p, value, t),
            3 => new Vector3(p, q, value),
            4 => new Vector3(t, p, value),
            _ => new Vector3(value, p, q)
        };
    }

    private static int AddTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        Ps2Vertex[] verts,
        bool startsOnOddOutputSlot)
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

            Ps2Vertex a;
            Ps2Vertex b;
            Ps2Vertex c;
            VERTEX va;
            VERTEX vb;
            VERTEX vc;

            if (((localIndex + parityBias) & 1) == 0)
            {
                a = verts[i - 2];
                b = verts[i - 1];
                c = verts[i];
                va = MakeVertex(a);
                vb = MakeVertex(b);
                vc = MakeVertex(c);
            }
            else
            {
                a = verts[i - 1];
                b = verts[i - 2];
                c = verts[i];
                va = MakeVertex(a);
                vb = MakeVertex(b);
                vc = MakeVertex(c);
            }

            if (IsDegenerate(a, b, c))
                continue;

            prim.AddTriangle(va, vb, vc);
            count++;
        }

        return count;
    }

    private static VERTEX MakeVertex(in Ps2Vertex vertex)
    {
        var normal = Vector3.UnitY;
        if (vertex.HasNormal)
        {
            var len = vertex.Normal.Length();
            normal = len > 0.001f ? vertex.Normal / len : Vector3.UnitY;
        }

        var uv = vertex.HasUV ? new Vector2(vertex.U, 1f - vertex.V) : Vector2.Zero;
        return new VERTEX(
            new VertexPositionNormal(vertex.Position, normal),
            new VertexColor1Texture1(Vector4.One, uv));
    }

    private static bool IsDegenerate(in Ps2Vertex a, in Ps2Vertex b, in Ps2Vertex c)
    {
        const float epsilon = 1e-8f;

        if (Vector3.DistanceSquared(a.Position, b.Position) <= epsilon ||
            Vector3.DistanceSquared(b.Position, c.Position) <= epsilon ||
            Vector3.DistanceSquared(a.Position, c.Position) <= epsilon)
        {
            return true;
        }

        var cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        return cross.LengthSquared() <= epsilon;
    }
}
