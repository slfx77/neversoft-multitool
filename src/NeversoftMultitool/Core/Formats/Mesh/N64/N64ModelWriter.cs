using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Populates a <see cref="ModelDocument" /> from a carved N64 model
///     bundle. The bundle splits the same way the Xbox DDM path does — a
///     placement/skeleton container (<c>geometry.psx.n64</c>) plus a separate
///     geometry container (<c>group2/</c>) — so the skeleton, bone names and
///     hierarchy come from the shell exactly as they do for a PS1 character,
///     and the render geometry from the bank
///     (<see cref="N64RenderBankFile" />).
///     <para>
///         Per-face texture binding is still open — the engine issues it
///         around the display list rather than inside it — so faces carry
///         their authored vertex colours under one unlit material.
///         <see cref="N64ModelRenderMetadata" /> records the bank id, its byte
///         size and whether geometry decoded, so a bundle that produced only a
///         rig is distinguishable from a genuinely empty one.
///     </para>
/// </summary>
public static class N64ModelWriter
{
    /// <summary>
    ///     The N64 build stores vertices as <c>trunc(PS1raw / k)</c>, k = 8 for
    ///     the animated (super) models the PS1 stores ×16 and k = 1 elsewhere,
    ///     so world units are <c>raw × k / shellScaleDivisor</c>. k is inferred
    ///     from the shell's super flag — the correlation is 45/46 across the
    ///     measured corpus and no field carrying it has been found.
    /// </summary>
    private static float WorldScale(PsxMeshFile shell)
    {
        var k = shell.IsSuperModel ? 8f : 1f;
        return k / shell.ScaleDivisor;
    }

    public static void Populate(ModelDocument document, N64ModelNativeSource source)
    {
        var shell = source.Shell;

        // Object table + HIER parents alone; no mesh data is consulted.
        document.Skeletons.Add(PsxSkinnedGeometryWriter.BuildPsxSkeleton(
            shell, pshFile: null, flatSkeleton: false, flatBoneIndices: null));

        var meshes = source.RenderBank != null
            ? N64RenderBankFile.Parse(source.RenderBank)
            : [];

        var material = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
        {
            Name = "n64_vertex_colour",
            BaseColor = Vector4.One,
            // Texture binding is issued by the engine around the display list,
            // not encoded in it, so the art is not yet resolvable per face.
            // Vertex colours carry the authored shading in the meantime.
            Unlit = true
        });

        var scale = WorldScale(shell);
        var emitted = 0;
        for (var i = 0; i < meshes.Count; i++)
        {
            if (EmitMesh(document, meshes[i], material, scale, i))
                emitted++;
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        document.NativeMetadata.Add(new N64ModelRenderMetadata(
            source.RenderBankId,
            source.RenderBank?.Length ?? 0,
            shell.Objects.Count,
            GeometryDecoded: emitted > 0));
    }

    /// <summary>
    ///     Emits one render-bank mesh node, split into a node per
    ///     <c>G_MTX</c> index so the parts stay separable in the exported
    ///     scene.
    ///     <para>
    ///         Pool vertices are already in MODEL space — the matrix index
    ///         selects the runtime animation matrix, not an authored placement
    ///         offset. Measured: translating each part by its object-table
    ///         offset scatters the model (the figure's limbs separate and its
    ///         height overshoots the PS1 sibling), while emitting the pool
    ///         as-is reproduces a coherent character. So no offset is applied.
    ///     </para>
    /// </summary>
    private static bool EmitMesh(
        ModelDocument document,
        N64RenderBankFile.N64RenderMesh mesh,
        int materialIndex,
        float scale,
        int index)
    {
        if (mesh.Triangles.Count == 0)
            return false;

        var emitted = false;
        foreach (var part in mesh.Triangles.GroupBy(static t => t.MatrixIndex).OrderBy(static g => g.Key))
        {
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var triangle in part)
            {
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices, indices,
                    ToVertex(mesh.Vertices[triangle.V0], scale),
                    ToVertex(mesh.Vertices[triangle.V1], scale),
                    ToVertex(mesh.Vertices[triangle.V2], scale));
            }

            if (indices.Count == 0)
                continue;

            var name = $"n64_{index:D4}_part{part.Key:D3}";
            var modelMesh = new ModelMesh { Name = name };
            ModelDocumentGeometryAdapter.AddPrimitive(modelMesh, name, materialIndex, vertices, indices);
            ModelDocumentGeometryAdapter.AddMeshNode(document, name, modelMesh);
            emitted = true;
        }

        return emitted;
    }

    /// <summary>
    ///     Converts one F3DEX2 vertex. Position uses the same handedness map as
    ///     every PS1 export (<c>X, −Y, −Z</c>) so N64 and PS1 conversions of the
    ///     same model land in the same orientation. UVs are S10.5 texels
    ///     (÷32); with no per-face texture binding yet they are normalised by a
    ///     nominal 256-texel sheet — the value the corpus's [0, 256] operand
    ///     range implies — and will be recomputed from real dimensions once
    ///     texture binding is resolved.
    /// </summary>
    private static ModelVertex ToVertex(N64RenderBankFile.N64Vertex vertex, float scale)
    {
        const float texelScale = 32f * 256f;
        return new ModelVertex
        {
            Position = PsxMeshSemantics.ToGltfPosition(
                new Vector3(vertex.X * scale, vertex.Y * scale, vertex.Z * scale)),
            Normal = Vector3.UnitY,
            Color = new Vector4(vertex.R / 255f, vertex.G / 255f, vertex.B / 255f, vertex.A / 255f),
            TexCoord = new Vector2(vertex.S / texelScale, vertex.T / texelScale)
        };
    }
}

/// <summary>
///     Carried into the export so a caller can tell an N64 bundle's rig-only
///     state from a genuinely empty model.
/// </summary>
public sealed record N64ModelRenderMetadata(
    uint? RenderBankId,
    int RenderBankBytes,
    int ObjectCount,
    bool GeometryDecoded) : NativeRenderMetadata("n64Model");
