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
///         Textures bind per geometry group: the descriptor's word 0 is a
///         GLOBAL texture-dictionary slot index (gated by kind bit 0; 0 means
///         untextured), and blob B's PS1 face flag word supplies the blend
///         state. <see cref="N64ModelRenderMetadata" /> reports how many faces
///         resolved a texture so coverage is stated, not assumed.
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

        var materials = new N64MaterialCache(document, source.TextureProvider);
        var scale = WorldScale(shell);
        var emitted = 0;
        // Placement is OBJECT-driven, exactly as the PS1 writer does it: each
        // object places the mesh its MeshIndex names, at its own offset. A mesh
        // no object references is never drawn (a Downhill Jam shell carries 883
        // meshes for 642 objects), and one mesh may be placed more than once.
        var byNode = meshes.ToDictionary(static m => m.NodeIndex);
        for (var objectIndex = 0; objectIndex < shell.Objects.Count; objectIndex++)
        {
            var obj = shell.Objects[objectIndex];
            if (!byNode.TryGetValue(obj.MeshIndex, out var mesh))
                continue;

            if (EmitMesh(document, mesh, materials, scale, objectIndex, shell))
                emitted++;
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        document.NativeMetadata.Add(new N64ModelRenderMetadata(
            source.RenderBankId,
            source.RenderBank?.Length ?? 0,
            shell.Objects.Count,
            GeometryDecoded: emitted > 0,
            materials.TexturedFaces,
            materials.UntexturedFaces));
    }

    /// <summary>
    ///     Emits one render-bank mesh node, split into a node per
    ///     <c>G_MTX</c> index so the parts stay separable in the exported
    ///     scene.
    ///     <para>
    ///         The G_MTX index selects the runtime animation matrix, so it
    ///         separates parts but carries no placement of its own — the
    ///         placing object's authored offset does that. Node vertices are
    ///         MESH-LOCAL: verified on c_kart, whose box was the right size but
    ///         displaced by exactly its object's (-10, 9, -92)/2.25, and which
    ///         matches PS1 to ~0.2 (the port's trunc(raw/8) quantisation) once
    ///         the offset is applied.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     World offset for one corner. G_MTX selects a matrix RELATIVE to the
    ///     placing object: a level node draws with matrix 0 and takes its
    ///     placer's offset, while a character is ONE node whose matrices walk
    ///     the object table from the placer. Applied PER CORNER because the RSP
    ///     transforms a vertex when it is loaded, so a triangle bridging two
    ///     rigid parts carries two matrices.
    /// </summary>
    private static Vector3 CornerOffset(
        PsxMeshFile shell, int objectIndex, N64RenderBankFile.N64Corner corner)
    {
        return ObjectOffset(shell, objectIndex + corner.MatrixIndex);
    }

    /// <summary>Offset of an object, or the origin when the index is outside the table.</summary>
    private static Vector3 ObjectOffset(PsxMeshFile shell, int index)
    {
        return (uint)index < (uint)shell.Objects.Count
            ? PsxMeshSemantics.GetObjectOffset(shell, shell.Objects[index])
            : Vector3.Zero;
    }

    private static bool EmitMesh(
        ModelDocument document,
        N64RenderBankFile.N64RenderMesh mesh,
        N64MaterialCache materials,
        float scale,
        int objectIndex,
        PsxMeshFile shell)
    {
        if (mesh.Triangles.Count == 0)
            return false;

        // Split by part (G_MTX) and then by material, so each primitive binds
        // exactly one texture with one blend state.
        var emitted = false;
        foreach (var part in mesh.Triangles.GroupBy(static t => t.MatrixIndex).OrderBy(static g => g.Key))
        {
            // G_MTX selects a matrix RELATIVE to the placing object: a level
            // node draws with matrix 0 and takes its placer's offset, while a
            // character is ONE node whose matrices 0..N-1 walk the object table
            // from the placer. object[placer + matrix] satisfies both - it
            // reproduces the PS1 skater's height (93.3 vs 93.8) where using no
            // offset gives 40.9.
            var modelMesh = new ModelMesh { Name = $"n64_{objectIndex:D4}_part{part.Key:D3}" };
            var batches = new Dictionary<int, (List<ModelVertex> Vertices, List<int> Indices)>();

            foreach (var triangle in part)
            {
                // The bank ships the PS1's undrawn faces — collision blockers,
                // trigger volumes, camera zones — as ordinary geometry. Blob B
                // carries the same DISC flag word the PS1 file does, so the
                // identical rule drops them (measured: 8.8% of THPS2 faces).
                if (PsxFaceFlags.IsInvisible(triangle.FaceFlags))
                    continue;

                                // Vertex alpha is a real translucency source: light shafts and
                // glows are untextured, vertex-coloured and fade via alpha, and
                // 11% of THPS1 vertices are non-opaque.
                var translucent = !mesh.HasNormals && (
                    mesh.Vertices[triangle.V0].A < 255 ||
                    mesh.Vertices[triangle.V1].A < 255 ||
                    mesh.Vertices[triangle.V2].A < 255);
                var (materialIndex, size) = materials.Resolve(triangle, mesh.HasNormals, translucent);
                if (!batches.TryGetValue(materialIndex, out var batch))
                {
                    batch = ([], []);
                    batches[materialIndex] = batch;
                }

                ModelDocumentGeometryAdapter.AddTriangle(
                    batch.Vertices, batch.Indices,
                    ToVertex(mesh, triangle.C0, scale, size, CornerOffset(shell, objectIndex, triangle.C0)),
                    ToVertex(mesh, triangle.C1, scale, size, CornerOffset(shell, objectIndex, triangle.C1)),
                    ToVertex(mesh, triangle.C2, scale, size, CornerOffset(shell, objectIndex, triangle.C2)));
            }

            foreach (var (materialIndex, batch) in batches.OrderBy(static b => b.Key))
            {
                if (batch.Indices.Count == 0)
                    continue;
                ModelDocumentGeometryAdapter.AddPrimitive(
                    modelMesh, $"{modelMesh.Name}_m{materialIndex:D3}",
                    materialIndex, batch.Vertices, batch.Indices);
            }

            if (modelMesh.Primitives.Count == 0)
                continue;

            ModelDocumentGeometryAdapter.AddMeshNode(document, modelMesh.Name, modelMesh);
            emitted = true;
        }

        return emitted;
    }

    /// <summary>
    ///     Converts one F3DEX2 vertex. Position uses the same handedness map as
    ///     every PS1 export (<c>X, −Y, −Z</c>) so N64 and PS1 conversions of the
    ///     same model land in the same orientation. UVs are S10.5 texels (÷32)
    ///     normalised by the BOUND texture's real dimensions — corpus UV spans
    ///     cluster at 63/127/255, i.e. texel coordinates running 0..N−1 over
    ///     64/128/256-wide sheets, so a fixed divisor is wrong for most faces.
    ///     UVs come from the CORNER, which carries any G_MODIFYVTX override.
    /// </summary>
    private static ModelVertex ToVertex(
        N64RenderBankFile.N64RenderMesh mesh,
        N64RenderBankFile.N64Corner corner,
        float scale,
        (int Width, int Height) size,
        Vector3 offset)
    {
        var vertex = mesh.Vertices[corner.Vertex];
        var hasNormals = mesh.HasNormals;
        var uScale = 32f * Math.Max(1, size.Width);
        var vScale = 32f * Math.Max(1, size.Height);

        // F3DEX2 reuses the trailing four bytes for either a lit normal or an
        // authored colour. A lit pool has no colour to export (the shade comes
        // from the light), so it gets white and a real normal.
        var normal = Vector3.UnitY;
        var colour = Vector4.One;
        if (hasNormals)
        {
            var n = new Vector3((sbyte)vertex.R, (sbyte)vertex.G, (sbyte)vertex.B) / 127f;
            if (n.LengthSquared() > 1e-6f)
                normal = Vector3.Normalize(PsxMeshSemantics.ToGltfPosition(n));
        }
        else
        {
            colour = new Vector4(vertex.R / 255f, vertex.G / 255f, vertex.B / 255f, vertex.A / 255f);
        }

        return new ModelVertex
        {
            Position = PsxMeshSemantics.ToGltfPosition(
                new Vector3(vertex.X * scale, vertex.Y * scale, vertex.Z * scale) + offset),
            Normal = normal,
            Color = colour,
            // Corner ST, not the pool vertex's: G_MODIFYVTX can rewrite it.
            TexCoord = new Vector2(corner.S / uScale, corner.T / vScale)
        };
    }
}

/// <summary>
///     Builds one glTF material per (texture slot, blend state) a bank
///     references, mirroring the PS1 material key so the same texture used
///     opaque and semi-transparent yields distinct materials. Blend semantics
///     come from the PS1 face flag word blob B carries: bit 6 sets ABE, bits
///     7-8 the ABR rate, bit 9 disables backface culling.
/// </summary>
internal sealed class N64MaterialCache(
    ModelDocument document,
    Func<int, N64ModelCompanions.N64ResolvedTexture?> textureProvider)
{
    private readonly Dictionary<(int Slot, bool Semi, bool DoubleSided, int Rate, bool Lit, bool VertexAlpha), int>
        _materials = [];
    private readonly Dictionary<int, (int Width, int Height)> _sizes = [];

    public int TexturedFaces { get; private set; }
    public int UntexturedFaces { get; private set; }

    public (int MaterialIndex, (int Width, int Height) Size) Resolve(
        N64RenderBankFile.N64Triangle triangle,
        bool lit,
        bool translucentVertices)
    {
        var flags = triangle.FaceFlags;
        var semi = (flags & PsxFaceFlags.SemiTransparent) != 0;
        var doubleSided = (flags & PsxFaceFlags.DoubleSided) != 0;
        var rate = semi ? (flags & PsxFaceFlags.BlendRateMask) >> 7 : 0;
        var key = (triangle.TextureSlot, semi, doubleSided, rate, lit, translucentVertices);

        if (_materials.TryGetValue(key, out var existing))
        {
            Count(triangle.TextureSlot);
            return (existing, _sizes.GetValueOrDefault(triangle.TextureSlot, (1, 1)));
        }

        var texture = triangle.TextureSlot > 0 ? textureProvider(triangle.TextureSlot) : null;
        var size = texture != null ? (texture.Width, texture.Height) : (1, 1);
        _sizes[triangle.TextureSlot] = size;

        var blendSuffix = semi ? $"__st{rate}" : string.Empty;
        var sideSuffix = doubleSided ? "__2sided" : string.Empty;
        var baseName = texture?.Name ?? "n64_untextured";
        var material = new RenderMaterial
        {
            Name = baseName + blendSuffix + sideSuffix,
            BaseColor = Vector4.One,
            DoubleSided = doubleSided,
            // Always unlit, like the PS1 path. The console shades these
            // surfaces diffusely with no specular term at all, whereas glTF's
            // metallic-roughness model always adds a dielectric highlight -
            // which reads as glossiness the game never had. Normals are still
            // exported for consumers that want them.
            Unlit = true,
            AlphaMode = ResolveAlphaMode(semi || translucentVertices, texture),
            // 1-bit art: keep any texel the console would have drawn.
            AlphaCutoff = 0.5f
        };

        var index = ModelDocumentGeometryAdapter.AddMaterial(document, material);
        if (texture != null)
        {
            var textureIndex = ModelDocumentGeometryAdapter.AddTexture(
                document, texture.Name, texture.Png, (uint)triangle.TextureSlot);
            document.Materials[index].TextureIndex = textureIndex;
        }

        _materials[key] = index;
        Count(triangle.TextureSlot);
        return (index, size);
    }

    /// <summary>
    ///     A face flagged semi-transparent - or one whose vertices carry
    ///     alpha - blends. Otherwise the TEXTURE decides: N64 art cuts wheels, steering wheels and foliage out of
    ///     their quads with fully transparent texels (1-bit RGBA5551 alpha or
    ///     A=0 palette entries), which is alpha TESTING, not blending. Only
    ///     genuinely partial alpha needs a blend.
    /// </summary>
    private static ModelAlphaMode ResolveAlphaMode(bool semi, N64ModelCompanions.N64ResolvedTexture? texture)
    {
        if (semi || texture is { HasGraduatedAlpha: true })
            return ModelAlphaMode.Blend;
        return texture is { HasCutout: true } ? ModelAlphaMode.Mask : ModelAlphaMode.Opaque;
    }

    private void Count(int slot)
    {
        if (slot > 0)
            TexturedFaces++;
        else
            UntexturedFaces++;
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
    bool GeometryDecoded,
    int TexturedFaces = 0,
    int UntexturedFaces = 0) : NativeRenderMetadata("n64Model");
