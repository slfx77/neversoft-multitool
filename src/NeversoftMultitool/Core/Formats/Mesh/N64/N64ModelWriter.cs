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

            if (EmitMesh(document, mesh, materials, scale, objectIndex,
                    PsxMeshSemantics.GetObjectOffset(shell, obj)))
            {
                emitted++;
            }
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
    private static bool EmitMesh(
        ModelDocument document,
        N64RenderBankFile.N64RenderMesh mesh,
        N64MaterialCache materials,
        float scale,
        int index,
        Vector3 offset)
    {
        if (mesh.Triangles.Count == 0)
            return false;

        // Split by part (G_MTX) and then by material, so each primitive binds
        // exactly one texture with one blend state.
        var emitted = false;
        foreach (var part in mesh.Triangles.GroupBy(static t => t.MatrixIndex).OrderBy(static g => g.Key))
        {
            var modelMesh = new ModelMesh { Name = $"n64_{index:D4}_part{part.Key:D3}" };
            var batches = new Dictionary<int, (List<ModelVertex> Vertices, List<int> Indices)>();

            foreach (var triangle in part)
            {
                // The bank ships the PS1's undrawn faces — collision blockers,
                // trigger volumes, camera zones — as ordinary geometry. Blob B
                // carries the same DISC flag word the PS1 file does, so the
                // identical rule drops them (measured: 8.8% of THPS2 faces).
                if (PsxFaceFlags.IsInvisible(triangle.FaceFlags))
                    continue;

                var (materialIndex, size) = materials.Resolve(triangle, mesh.HasNormals);
                if (!batches.TryGetValue(materialIndex, out var batch))
                {
                    batch = ([], []);
                    batches[materialIndex] = batch;
                }

                ModelDocumentGeometryAdapter.AddTriangle(
                    batch.Vertices, batch.Indices,
                    ToVertex(mesh, triangle.C0, scale, size, offset),
                    ToVertex(mesh, triangle.C1, scale, size, offset),
                    ToVertex(mesh, triangle.C2, scale, size, offset));
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
    private readonly Dictionary<(int Slot, bool Semi, bool DoubleSided, int Rate, bool Lit), int> _materials = [];
    private readonly Dictionary<int, (int Width, int Height)> _sizes = [];

    public int TexturedFaces { get; private set; }
    public int UntexturedFaces { get; private set; }

    public (int MaterialIndex, (int Width, int Height) Size) Resolve(
        N64RenderBankFile.N64Triangle triangle,
        bool lit)
    {
        var flags = triangle.FaceFlags;
        var semi = (flags & PsxFaceFlags.SemiTransparent) != 0;
        var doubleSided = (flags & PsxFaceFlags.DoubleSided) != 0;
        var rate = semi ? (flags & PsxFaceFlags.BlendRateMask) >> 7 : 0;
        var key = (triangle.TextureSlot, semi, doubleSided, rate, lit);

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
            // A pool carrying vertex COLOURS is pre-shaded, so it exports unlit
            // like the PS1 path. A pool carrying NORMALS is lit by the engine,
            // so leave it shadable and let the viewer light the normals.
            Unlit = !lit,
            AlphaMode = ResolveAlphaMode(semi, texture),
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
    ///     A face flagged semi-transparent blends. Otherwise the TEXTURE
    ///     decides: N64 art cuts wheels, steering wheels and foliage out of
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
