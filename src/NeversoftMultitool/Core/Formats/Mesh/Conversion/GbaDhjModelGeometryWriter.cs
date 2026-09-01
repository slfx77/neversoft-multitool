using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Converts a Downhill Jam GBA rider to the common model document.  Raw
///     vertices occupy 13 independent rigid-part spaces, so this writer requires
///     a decoded <c>0x50</c>-byte pose frame and applies the same X/Y/Z rotation
///     sequence as the game's ARM renderer before emitting any triangles.
///
///     <para>The palette/ramp binding remains unknown. Each authored face group
///     therefore receives an unlit debug colour; the source face byte remains available from
///     <see cref="GbaDhjModel.Face" /> and is not presented as RGB.</para>
/// </summary>
internal static class GbaDhjModelGeometryWriter
{
    /// <summary>Presentation scale chosen to match the existing THPS2 GBA export.</summary>
    internal const float Scale = 2f;

    // High-contrast, colour-blind-friendly-ish debug colours.  These identify the
    // 13 authored groups; they are not claimed to be the game's outfit palette.
    private static readonly Vector4[] GroupColours =
    [
        Rgb(0xE6, 0x8A, 0x8A), Rgb(0x55, 0xA8, 0xE0), Rgb(0x6B, 0xC4, 0x76),
        Rgb(0xE5, 0xB8, 0x4B), Rgb(0xA8, 0x75, 0xD1), Rgb(0x53, 0xC5, 0xB0),
        Rgb(0xE4, 0x79, 0xB7), Rgb(0x9E, 0xA5, 0xAD), Rgb(0xD3, 0x75, 0x4E),
        Rgb(0x72, 0x80, 0xD4), Rgb(0x99, 0xBD, 0x4F), Rgb(0xC4, 0x79, 0x55),
        Rgb(0x58, 0xB5, 0xCB)
    ];

    internal static ModelDocument Build(
        ReadOnlySpan<byte> rom,
        GbaDhjModel.ModelInfo model,
        GbaDhjModel.PoseFrame pose,
        string name)
    {
        var vertices = GbaDhjModel.ReadVertices(rom, model);
        var posedVertices = ApplyPose(vertices, model.VertexCounts, pose);
        var faces = GbaDhjModel.ReadFaces(rom, model);
        var document = new ModelDocument
        {
            Name = name,
            SourceKind = ModelSourceKind.Generic
        };
        var mesh = new ModelMesh { Name = name };

        foreach (var group in faces.GroupBy(static face => face.Group).OrderBy(static group => group.Key))
        {
            var groupIndex = group.Key;
            var materialIndex = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
            {
                Name = $"{name}_group_{groupIndex:D2}_debug",
                BaseColor = GroupColours[groupIndex % GroupColours.Length],
                DoubleSided = true,
                Unlit = true
            });

            var primitiveVertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var face in group)
            {
                var a = ToGlb(posedVertices[face.V0]);
                var b = ToGlb(posedVertices[face.V1]);
                var c = ToGlb(posedVertices[face.V2]);
                var normal = Vector3.Cross(b - a, c - a);
                normal = normal.LengthSquared() < 1e-12f
                    ? Vector3.UnitY
                    : Vector3.Normalize(normal);
                ModelDocumentGeometryAdapter.AddTriangle(
                    primitiveVertices,
                    indices,
                    new ModelVertex(a, normal, Vector4.One, Vector2.Zero),
                    new ModelVertex(b, normal, Vector4.One, Vector2.Zero),
                    new ModelVertex(c, normal, Vector4.One, Vector2.Zero));
            }

            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, $"group_{groupIndex:D2}", materialIndex, primitiveVertices, indices);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, name, mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        return document;
    }

    /// <summary>
    ///     Assemble the 13 raw rigid-part vertex groups in model space.  This is
    ///     a direct translation of the ARM routine copied from ROM 0x080009DC to
    ///     IWRAM 0x030045BC in the retail US build.  Angles are bytes where 256 is
    ///     one turn; the game uses a 512-scale sine table, represented here with
    ///     floating-point trigonometry before the final export quantization.
    /// </summary>
    internal static Vector3[] ApplyPose(
        IReadOnlyList<GbaDhjModel.Vertex> vertices,
        IReadOnlyList<ushort> vertexCounts,
        GbaDhjModel.PoseFrame pose)
    {
        if (vertexCounts.Count != GbaDhjModel.GroupCount
            || pose.Parts.Length != GbaDhjModel.GroupCount
            || vertexCounts.Sum(static count => count) != vertices.Count)
        {
            throw new InvalidDataException("Downhill Jam model and pose groups do not match");
        }

        var result = new Vector3[vertices.Count];
        var vertexIndex = 0;
        for (var group = 0; group < vertexCounts.Count; group++)
        {
            var part = pose.Parts[group];
            var xAngle = ToRadians(part.RotationX);
            var yAngle = ToRadians(part.RotationY);
            var zAngle = ToRadians(part.RotationZ);
            var sinX = MathF.Sin(xAngle);
            var cosX = MathF.Cos(xAngle);
            var sinY = MathF.Sin(yAngle);
            var cosY = MathF.Cos(yAngle);
            var sinZ = MathF.Sin(zAngle);
            var cosZ = MathF.Cos(zAngle);

            for (var inGroup = 0; inGroup < vertexCounts[group]; inGroup++, vertexIndex++)
            {
                var vertex = vertices[vertexIndex];

                // X stage.
                var y1 = cosX * vertex.Y - sinX * vertex.Z;
                var z1 = sinX * vertex.Y + cosX * vertex.Z;

                // Y stage.  The z sign at zero rotation is intentional: this is
                // the exact handedness conversion performed by the game.
                var x2 = cosY * vertex.X + sinY * z1;
                var z2 = sinY * vertex.X - cosY * z1;

                // Z stage, then the independently-authored rigid-part origin.
                var x3 = cosZ * x2 - sinZ * y1;
                var y3 = cosZ * y1 + sinZ * x2;
                result[vertexIndex] = new Vector3(
                    part.TranslationX + x3,
                    part.TranslationY + y3,
                    -part.TranslationZ + z2);
            }
        }

        return result;
    }

    // Posed source Z runs downward from the board toward the rider's upper body.
    // (x,-z,-y) produces an upright, right-handed GLB and uses the same world
    // scale as the existing THPS2 GBA model conversion.
    internal static Vector3 ToGlb(Vector3 position) =>
        new(position.X * Scale, -position.Z * Scale, -position.Y * Scale);

    private static float ToRadians(byte angle) => angle * (2f * MathF.PI / 256f);

    private static Vector4 Rgb(byte red, byte green, byte blue) =>
        new(red / 255f, green / 255f, blue / 255f, 1f);
}
