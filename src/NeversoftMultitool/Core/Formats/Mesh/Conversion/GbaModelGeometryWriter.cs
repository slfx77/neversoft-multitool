using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds a THPS2 GBA character as a 3D model: the shared morph-target skater
///     mesh with the character's own outfit colours. Faces group into one
///     primitive per material, coloured with the mid shade of the material's
///     palette ramp — the runtime lights across the ramp; the mid shade is the
///     authored colour (verified: it dresses the skater anatomically — skin,
///     shirt with logo, pants, shoes, deck). Unlit, like the console's output.
///
///     <para>Without morph targets this writes the plain static export: the
///     pool's frame 0, a neutral standing pose. With them, the same base mesh
///     carries one clip's distinct frames as glTF morph targets — see
///     <see cref="GbaAnimatedModelWriter" />.</para>
/// </summary>
internal static class GbaModelGeometryWriter
{
    /// <summary>Model-unit → GLB scale (the skater is ~77 s8 units tall; ×2 keeps
    ///     character models in the viewer's usual size range).</summary>
    public const float Scale = 2f;

    private const int StaticFrame = 0;

    public static void Populate(
        ModelDocument document, GbaModelNativeSource native, GbaMorphTargets? morphTargets = null)
    {
        var rom = native.Rom;
        var model = GbaSkaterModel.TryLocate(rom)
                    ?? throw new InvalidDataException("This ROM does not carry the skater model complex");

        // All 15 characters share one mesh, so the parts this one does not wear
        // (Muska's hood, the female skaters' ponytail, the leg style they did
        // not pick) are switched off by the roster record's part mask.
        var partMask = GbaSkaterModel.GetPartMask(rom, model, native.CharacterIndex);
        var faces = GbaSkaterModel.ReadFaces(rom, model)
            .Where(face => (partMask >> face.SubObject & 1) != 0)
            .ToList();
        var verts = GbaSkaterModel.ReadFrameVertices(rom, model, StaticFrame);
        var colors = GbaSkaterModel.TryGetMaterialColors(rom, model, native.CharacterIndex, native.Outfit)
                     ?? throw new InvalidDataException("The character's colour stream does not decode");

        var boneBase = SubObjectBases(model);
        // Morph deltas are keyed by base-vertex geometry, so a source vertex must
        // resolve to ONE (position, normal) pair. Per-face normals put two
        // distinct vertices on the same key; averaging per source vertex leaves
        // only pairs that never move apart, for which sharing is exact.
        var normals = morphTargets == null
            ? null
            : AverageVertexNormals(model, faces, verts, boneBase);

        var mesh = new ModelMesh { Name = native.CharacterName };

        // One primitive per used material, so each carries its authored colour.
        foreach (var group in faces.GroupBy(static f => f.Material).OrderBy(static g => g.Key))
        {
            var material = group.Key;
            var rgba = material < colors.Length ? colors[material] : [200, 200, 200, 255];
            var materialIndex = document.Materials.Count;
            document.Materials.Add(new RenderMaterial
            {
                Name = $"{native.CharacterName}_m{material:D2}",
                BaseColor = new Vector4(rgba[0] / 255f, rgba[1] / 255f, rgba[2] / 255f, 1f)
            });

            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            var sources = morphTargets == null ? null : new List<int>();
            foreach (var face in group)
                AppendFace(vertices, indices, sources, verts, normals, boneBase, face);

            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, $"m{material:D2}", materialIndex, vertices, indices,
                morphTargets: sources == null ? null : BuildTargets(morphTargets, sources));
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, native.CharacterName, mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    /// <summary>Per-sub-object base into the flat source-vertex numbering.</summary>
    internal static int[] SubObjectBases(GbaSkaterModel.ModelInfo model)
    {
        var bases = new int[GbaSkaterModel.SubObjectCount];
        var total = 0;
        for (var sub = 0; sub < GbaSkaterModel.SubObjectCount; sub++)
        {
            bases[sub] = total;
            total += model.VertCounts[sub];
        }

        return bases;
    }

    // Model space is z-up; GLB is y-up right-handed: (x, z, -y) preserves chirality.
    // Shared with GbaAnimatedModelWriter so base mesh and targets agree exactly.
    internal static Vector3 ToGlb(sbyte[] v) => new(v[0] * Scale, v[2] * Scale, -v[1] * Scale);

    private static void AppendFace(
        List<ModelVertex> vertices, List<int> indices, List<int>? sources,
        sbyte[][][] verts, Vector3[]? normals, int[] boneBase, GbaSkaterModel.Face face)
    {
        var sub = verts[face.SubObject];
        if (face.V0 >= sub.Length || face.V1 >= sub.Length || face.V2 >= sub.Length)
            return;
        var a = ToGlb(sub[face.V0]);
        var b = ToGlb(sub[face.V1]);
        var c = ToGlb(sub[face.V2]);
        var faceNormal = Vector3.Cross(b - a, c - a);
        faceNormal = faceNormal.LengthSquared() < 1e-12f
            ? Vector3.UnitY
            : Vector3.Normalize(faceNormal);

        var s0 = boneBase[face.SubObject] + face.V0;
        var s1 = boneBase[face.SubObject] + face.V1;
        var s2 = boneBase[face.SubObject] + face.V2;
        var n0 = normals == null ? faceNormal : normals[s0];
        var n1 = normals == null ? faceNormal : normals[s1];
        var n2 = normals == null ? faceNormal : normals[s2];

        var before = vertices.Count;
        ModelDocumentGeometryAdapter.AddTriangle(vertices, indices,
            new ModelVertex(a, n0, Vector4.One, Vector2.Zero),
            new ModelVertex(b, n1, Vector4.One, Vector2.Zero),
            new ModelVertex(c, n2, Vector4.One, Vector2.Zero));
        if (sources == null || vertices.Count == before)
            return; // a degenerate triangle the adapter dropped

        sources.Add(s0);
        sources.Add(s1);
        sources.Add(s2);
    }

    /// <summary>
    ///     Redistributes source-vertex deltas onto one primitive's own corners.
    /// </summary>
    private static ModelMorphTarget[] BuildTargets(GbaMorphTargets morphTargets, List<int> sources)
    {
        var targets = new ModelMorphTarget[morphTargets.DeltasByTarget.Length];
        for (var t = 0; t < targets.Length; t++)
        {
            var source = morphTargets.DeltasByTarget[t];
            var deltas = new Vector3[sources.Count];
            for (var v = 0; v < sources.Count; v++)
                deltas[v] = source[sources[v]];
            targets[t] = new ModelMorphTarget
            {
                Name = $"{morphTargets.ClipName}_f{morphTargets.Frames[t]}",
                PositionDeltas = deltas
            };
        }

        return targets;
    }

    private static Vector3[] AverageVertexNormals(
        GbaSkaterModel.ModelInfo model, List<GbaSkaterModel.Face> faces, sbyte[][][] verts, int[] boneBase)
    {
        var normals = new Vector3[model.VertCounts.Sum(count => count)];
        foreach (var face in faces)
        {
            var sub = verts[face.SubObject];
            if (face.V0 >= sub.Length || face.V1 >= sub.Length || face.V2 >= sub.Length)
                continue;
            var a = ToGlb(sub[face.V0]);
            var normal = Vector3.Cross(ToGlb(sub[face.V1]) - a, ToGlb(sub[face.V2]) - a);
            if (normal.LengthSquared() < 1e-12f)
                continue;
            normal = Vector3.Normalize(normal);
            normals[boneBase[face.SubObject] + face.V0] += normal;
            normals[boneBase[face.SubObject] + face.V1] += normal;
            normals[boneBase[face.SubObject] + face.V2] += normal;
        }

        for (var i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() < 1e-12f
                ? Vector3.UnitY
                : Vector3.Normalize(normals[i]);
        return normals;
    }
}
