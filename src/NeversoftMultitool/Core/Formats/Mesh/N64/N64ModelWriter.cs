using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Populates a <see cref="ModelDocument" /> from a carved N64 model
///     bundle. The bundle splits the same way the Xbox DDM path does — a
///     placement/skeleton container (<c>NNN_&lt;name&gt;.psx.n64</c>) plus a separate
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
    ///     Everything one conversion needs that does not vary per triangle:
    ///     the shell it is placed against, the material cache, the raw-unit
    ///     scale, the ROM's light rig, the coplanar-overlay assignments, the
    ///     semi-transparent lift, and the selected matrix binding plan.
    /// </summary>
    private readonly record struct EmitContext(
        PsxMeshFile Shell,
        N64MaterialCache Materials,
        float Scale,
        N64LightRig? Rig,
        IReadOnlyDictionary<N64TriangleInstanceKey, N64CoplanarOverlayAssignment> Overlays,
        N64SemiTransparentLift? Lift,
        N64GeometryBindingPlan Binding);

    /// <summary>
    ///     How far a decal separates from the surface it covers, in RAW N64
    ///     units — half of one. Authored coordinates are s16 integers, so half a
    ///     unit cannot reach another surface, and expressing it in raw units
    ///     keeps it proportionate on super models, where the PS1 writer's fixed
    ///     0.25 export units would exceed a whole authored step. Both
    ///     separations use it: the draw-order offset opaque overlays carry, and
    ///     the geometric lift semi-transparent faces take.
    /// </summary>
    private const float DecalLiftInRawUnits = 0.5f;

    /// <summary>
    ///     The N64 build stores vertices as <c>trunc(PS1raw / k)</c>, so world
    ///     units are <c>raw × k / shellScaleDivisor</c>. The selected binding
    ///     plan owns k: ordinary supers retain ×8, ordinary non-supers ×1, and
    ///     the exact Spider-Man map payload profile proves the one ×1 super.
    /// </summary>
    private static float WorldScale(
        PsxMeshFile shell,
        N64GeometryBindingPlan binding)
    {
        return binding.VertexScaleFactor / shell.ScaleDivisor;
    }

    public static void Populate(
        ModelDocument document,
        N64ModelNativeSource source,
        IReadOnlyList<int>? animationIndices = null,
        bool includeAllAnimations = false)
    {
        var shell = source.Shell;

        // Object table + HIER parents alone; no mesh data is consulted.
        document.Skeletons.Add(PsxSkinnedGeometryWriter.BuildPsxSkeleton(
            shell, pshFile: null, flatSkeleton: false, flatBoneIndices: null));

        var meshes = source.RenderBank != null
            ? N64RenderBankFile.Parse(source.RenderBank)
            : [];

        // Payload profiles affect static geometry too. Resolve before looking
        // at animation selection so a rejected/absent clip cannot put the
        // exact ×1 map back on the ordinary ×8-super scale.
        var staticBinding = N64AnimatedModelGate.CreateStaticBindingPlan(
            source.ShellData,
            shell,
            source.RenderBank,
            source.RenderBankId,
            meshes);

        // Embedded 0x2A direct-matrix and 0x2C compressed clips share one
        // bounded plan. The structural path uses global G_MTX joints; the one
        // exact map profile uses placement-relative joints. Both prove every
        // emitted corner, and everything else remains rigid/relative.
        var animationsRequested = includeAllAnimations || animationIndices is { Count: > 0 };
        var animationPlan = animationsRequested
            ? N64AnimatedModelGate.TryOpen(
                source.ShellData,
                shell,
                source.RenderBank,
                source.RenderBankId,
                meshes)
            : null;
        var decodedAnimations = animationPlan != null
            ? DecodeAnimations(
                shell, animationPlan.Animations, animationIndices, includeAllAnimations)
            : [];
        if (decodedAnimations.Count > 0)
        {
            // Policies, not recovered N64 playback behavior: use the
            // established PSX 30 fps preview cadence, and for tweened 0x2A
            // endings use the shared CycleAnim wrap. N64 timing and per-clip
            // loop/clamp mode remain unproven. Translation deliberately stays
            // at shell.ScaleDivisor (/36 for a super). Only render vertices
            // receive the binding plan's render-vertex correction.
            PsxAnimationChannelWriter.PopulatePsxAnimations(
                document,
                shell,
                0,
                decodedAnimations,
                new PsxAnimationOptions(Fps: PsxAnimationBank.DefaultPreviewFps));
        }

        // A structurally eligible bank alone is not enough to alter geometry.
        // Invalid selections, failed decodes, and all-placeholder clips retain
        // the historical unskinned static document.
        var binding = document.Animations.Count > 0
            ? animationPlan!.Geometry
            : staticBinding;

        var materials = new N64MaterialCache(document, source.TextureProvider);
        var scale = WorldScale(shell, binding);
        var emitted = 0;
        // Mesh selection is OBJECT-driven, exactly as the PS1 writer does it:
        // each object selects the mesh its MeshIndex names. Static conversion
        // uses the placing object's relative matrix base. Successful animation
        // uses either the gate's structural global plan or its exact-payload
        // relative plan. A mesh no object references is never drawn (a
        // Downhill Jam shell carries 883 meshes for 642 objects), and one mesh
        // may be placed more than once on the static path.
        var byNode = meshes.ToDictionary(static m => m.NodeIndex);
        var placements = new List<(int ObjectIndex, N64RenderBankFile.N64RenderMesh Mesh)>();
        for (var objectIndex = 0; objectIndex < shell.Objects.Count; objectIndex++)
        {
            if (byNode.TryGetValue(shell.Objects[objectIndex].MeshIndex, out var mesh))
                placements.Add((objectIndex, mesh));
        }

        // Built from the same placement list the emit loop walks, so detector,
        // lift and writer provably see the identical triangle set.
        var candidates = BuildOverlayCandidates(placements, shell, scale, binding);
        var overlays = N64CoplanarOverlayDetector.FindGroups(candidates, scale);
        var lift = N64SemiTransparentLift.Build(candidates, DecalLiftInRawUnits * scale);

        var context = new EmitContext(
            shell, materials, scale, source.LightRig, overlays, lift, binding);
        foreach (var (objectIndex, mesh) in placements)
        {
            if (EmitMesh(document, mesh, objectIndex, context))
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

    private static List<(string Name, PsxAnimation Animation)> DecodeAnimations(
        PsxMeshFile shell,
        N64CompressedAnimationBank bank,
        IReadOnlyList<int>? requestedIndices,
        bool includeAllAnimations)
    {
        IReadOnlyList<int> indices = includeAllAnimations
            ? Enumerable.Range(0, bank.Entries.Count).ToArray()
            : requestedIndices ?? [];
        var seen = new HashSet<int>();
        var clips = new List<(string Name, PsxAnimation Animation)>();
        foreach (var index in indices)
        {
            if (!seen.Add(index) || (uint)index >= (uint)bank.Entries.Count)
                continue;

            try
            {
                clips.Add(($"anim_{index}", bank.DecodeSlot(index, shell.Objects.Count)));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException
                                       or IndexOutOfRangeException or OverflowException)
            {
                // A malformed slot is never allowed to borrow from its
                // neighbour. Keep valid siblings useful, but do not publish a
                // partial channel set for the bad slot.
            }
        }

        return clips;
    }

    /// <summary>
    ///     Emits one render-bank mesh node, split into a node per
    ///     <c>G_MTX</c> index so the parts stay separable in the exported
    ///     scene.
    ///     <para>
    ///         The selected <see cref="N64GeometryBindingPlan" /> decides
    ///         whether G_MTX is relative to the placing object (static or the
    ///         exact flat-map animation profile) or a global animation joint.
    ///         Node vertices are MESH-LOCAL: verified
    ///         on c_kart, whose box was the right size but displaced by exactly
    ///         its object's (-10, 9, -92)/2.25, and which matches PS1 to ~0.2
    ///         (the port's trunc(raw/8) quantisation) once the offset is applied.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     World offset for one corner. G_MTX is either relative to the placing
    ///     object or a global joint according to the admitted plan. The same
    ///     plan is used here, by overlay detection, and by semi-transparent lifting.
    ///     It is applied PER CORNER because the RSP transforms a vertex when it
    ///     is loaded, so a triangle may bridge two rigid parts.
    /// </summary>
    private static Vector3 CornerOffset(
        PsxMeshFile shell,
        int objectIndex,
        N64RenderBankFile.N64Corner corner,
        N64GeometryBindingPlan binding)
    {
        var offsetObjectIndex = binding.ResolveOffsetObjectIndexOrDefault(
            objectIndex, corner.MatrixIndex);
        return ObjectOffset(shell, offsetObjectIndex);
    }

    /// <summary>Offset of an object, or the origin when the index is outside the table.</summary>
    private static Vector3 ObjectOffset(PsxMeshFile shell, int index)
    {
        return (uint)index < (uint)shell.Objects.Count
            ? PsxMeshSemantics.GetObjectOffset(shell, shell.Objects[index])
            : Vector3.Zero;
    }

    /// <summary>
    ///     Export-space position of one corner, exactly as <see cref="ToVertex" />
    ///     computes it. The overlay detector must measure the geometry the
    ///     writer actually emits, so both go through this.
    /// </summary>
    private static Vector3 CornerPosition(
        N64RenderBankFile.N64RenderMesh mesh,
        N64RenderBankFile.N64Corner corner,
        float scale,
        Vector3 offset)
    {
        var vertex = mesh.Vertices[corner.Vertex];
        return PsxMeshSemantics.ToGltfPosition(
            new Vector3(vertex.X * scale, vertex.Y * scale, vertex.Z * scale) + offset);
    }

    /// <summary>
    ///     Flattens the placements into detector candidates, applying the same
    ///     invisible-face gate the emit loop uses — flagging a face that never
    ///     ships would split a mesh around geometry nobody sees.
    /// </summary>
    private static List<N64OverlayCandidateSource> BuildOverlayCandidates(
        List<(int ObjectIndex, N64RenderBankFile.N64RenderMesh Mesh)> placements,
        PsxMeshFile shell,
        float scale,
        N64GeometryBindingPlan binding)
    {
        var sources = new List<N64OverlayCandidateSource>();
        foreach (var (objectIndex, mesh) in placements)
        {
            for (var i = 0; i < mesh.Triangles.Count; i++)
            {
                var triangle = mesh.Triangles[i];
                if (PsxFaceFlags.IsInvisible(triangle.FaceFlags))
                    continue;

                sources.Add(new N64OverlayCandidateSource(
                    new N64TriangleInstanceKey(objectIndex, i),
                    [
                        CornerPosition(mesh, triangle.C0, scale,
                            CornerOffset(shell, objectIndex, triangle.C0, binding)),
                        CornerPosition(mesh, triangle.C1, scale,
                            CornerOffset(shell, objectIndex, triangle.C1, binding)),
                        CornerPosition(mesh, triangle.C2, scale,
                            CornerOffset(shell, objectIndex, triangle.C2, binding)),
                    ],
                    triangle.TextureSlot,
                    triangle.FaceFlags));
            }
        }

        return sources;
    }

    private static bool EmitMesh(
        ModelDocument document,
        N64RenderBankFile.N64RenderMesh mesh,
        int objectIndex,
        EmitContext context)
    {
        if (mesh.Triangles.Count == 0)
            return false;

        // Split by part (G_MTX), then by coplanar-overlay layer, then by
        // material, so each primitive binds one texture with one blend state
        // and each decal layer can carry its own draw order.
        var emitted = false;
        var indexed = mesh.Triangles
            .Select(static (triangle, index) => (Triangle: triangle, Index: index))
            .ToList();

        foreach (var part in indexed.GroupBy(static t => t.Triangle.MatrixIndex).OrderBy(static g => g.Key))
        {
            // Static geometry retains the established relative object+matrix
            // interpretation. A successfully decoded animation uses its
            // admitted plan for both bind placement and rigid joint influence.
            var baseName = $"n64_{objectIndex:D4}_part{part.Key:D3}";
            var layers = part.ToLookup(item =>
                context.Overlays.TryGetValue(new N64TriangleInstanceKey(objectIndex, item.Index), out var assignment)
                    ? (assignment.GroupId, assignment.DrawRank)
                    : (GroupId: -1, DrawRank: 0));

            foreach (var layer in layers.OrderBy(static l => l.Key))
            {
                var (groupId, rank) = layer.Key;
                MeshDrawOrderMetadata? drawOrder = null;
                var name = baseName;
                if (groupId >= 0)
                {
                    name += rank <= 1 ? $"__overlay{groupId:D2}" : $"__overlay{groupId:D2}_r{rank}";
                    var offset = OverlayLiftVector(layer, mesh, objectIndex, context) * rank;
                    drawOrder = new MeshDrawOrderMetadata(rank, rank, groupId, offset.X, offset.Y, offset.Z);
                }

                emitted |= EmitLayer(document, mesh, objectIndex, context, layer, name, drawOrder);
            }
        }

        return emitted;
    }

    /// <summary>
    ///     Emits one layer of a part: the shared batching path, unchanged, plus
    ///     the draw-order record when the layer is a decal.
    /// </summary>
    private static bool EmitLayer(
        ModelDocument document,
        N64RenderBankFile.N64RenderMesh mesh,
        int objectIndex,
        EmitContext context,
        IEnumerable<(N64RenderBankFile.N64Triangle Triangle, int Index)> layer,
        string name,
        MeshDrawOrderMetadata? drawOrder)
    {
        var modelMesh = new ModelMesh { Name = name };
        var batches = new Dictionary<int, (
            List<ModelVertex> Vertices,
            List<int> Indices,
            List<ModelBoneInfluences> Influences)>();

        foreach (var (triangle, _) in layer)
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
            var (materialIndex, size) = context.Materials.Resolve(triangle, translucent);
            if (!batches.TryGetValue(materialIndex, out var batch))
            {
                batch = ([], [], []);
                batches[materialIndex] = batch;
            }

            var (l0, l1, l2) = SemiTransparentLift(mesh, triangle, objectIndex, context);
            var v0 = ToVertex(mesh, triangle.C0, size, objectIndex, context, l0);
            var v1 = ToVertex(mesh, triangle.C1, size, objectIndex, context, l1);
            var v2 = ToVertex(mesh, triangle.C2, size, objectIndex, context, l2);
            if (context.Binding.IsSkinned)
            {
                ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                    batch.Vertices, batch.Indices, batch.Influences,
                    v0, ModelBoneInfluences.Single(
                        context.Binding.ResolveSkinJoint(objectIndex, triangle.C0.MatrixIndex)),
                    v1, ModelBoneInfluences.Single(
                        context.Binding.ResolveSkinJoint(objectIndex, triangle.C1.MatrixIndex)),
                    v2, ModelBoneInfluences.Single(
                        context.Binding.ResolveSkinJoint(objectIndex, triangle.C2.MatrixIndex)));
            }
            else
            {
                ModelDocumentGeometryAdapter.AddTriangle(
                    batch.Vertices, batch.Indices, v0, v1, v2);
            }
        }

        foreach (var (materialIndex, batch) in batches.OrderBy(static b => b.Key))
        {
            if (batch.Indices.Count == 0)
                continue;
            var skin = context.Binding.IsSkinned
                ? new ModelSkinBinding
                {
                    SkeletonIndex = 0,
                    Influences = batch.Influences.ToArray()
                }
                : null;
            var primitive = ModelDocumentGeometryAdapter.AddPrimitive(
                modelMesh, $"{modelMesh.Name}_m{materialIndex:D3}",
                materialIndex, batch.Vertices, batch.Indices, skin);
            if (primitive != null && drawOrder != null)
                primitive.NativeMetadata.Add(drawOrder);
        }

        if (modelMesh.Primitives.Count == 0)
            return false;

        ModelDocumentGeometryAdapter.AddMeshNode(document, modelMesh.Name, modelMesh);
        return true;
    }

    /// <summary>
    ///     Which way, and how far, a decal layer separates from the surface it
    ///     covers. Direction is the layer's own outward normal — and it must be
    ///     <c>cross(p1-p0, p2-p0)</c>, because the N64 writer emits corners
    ///     unmodified (the reversal already happened in the display-list
    ///     expander), unlike the PS1 writer whose <c>AddPsxFace</c> emits
    ///     (v0, v2, v1) and therefore needs the opposite cross product. Copying
    ///     the PS1 expression here would push every decal INTO its surface.
    ///     <para>
    ///         Magnitude is half a raw N64 unit: authored coordinates are s16
    ///         integers, so half a unit cannot cross another surface, and it
    ///         stays proportionate on super models where the PS1's fixed 0.25
    ///         would exceed a whole unit. The viewer's logarithmic depth buffer
    ///         resolves it comfortably.
    ///     </para>
    /// </summary>
    private static Vector3 OverlayLiftVector(
        IEnumerable<(N64RenderBankFile.N64Triangle Triangle, int Index)> layer,
        N64RenderBankFile.N64RenderMesh mesh,
        int objectIndex,
        EmitContext context)
    {
        foreach (var (triangle, _) in layer)
        {
            var (p0, p1, p2) = CornerPositions(mesh, triangle, objectIndex, context);
            var normal = Vector3.Cross(p1 - p0, p2 - p0);
            var length = normal.Length();
            if (length > 1e-5f)
                return normal / length * (DecalLiftInRawUnits * context.Scale);
        }

        return Vector3.Zero;
    }

    /// <summary>
    ///     Per-corner lift for a semi-transparent triangle, or zero when the
    ///     face is opaque or the model has no semi-transparent geometry.
    ///     <para>
    ///         This is the PS1 writer's blanket lift, and it is what resolves
    ///         the decals the coplanar detector deliberately leaves alone.
    ///         Corners lift along the file's POSITION-AVERAGED semi-transparent
    ///         normals rather than this face's own, so connected curved surfaces
    ///         translate together instead of tearing at shared edges; the face
    ///         normal is only the fallback. Opaque decals are NOT lifted — they
    ///         separate through draw-order metadata, leaving their vertices at
    ///         the authored positions.
    ///     </para>
    /// </summary>
    private static (Vector3 C0, Vector3 C1, Vector3 C2) SemiTransparentLift(
        N64RenderBankFile.N64RenderMesh mesh,
        N64RenderBankFile.N64Triangle triangle,
        int objectIndex,
        EmitContext context)
    {
        if (context.Lift == null || (triangle.FaceFlags & PsxFaceFlags.SemiTransparent) == 0)
            return default;

        var (p0, p1, p2) = CornerPositions(mesh, triangle, objectIndex, context);
        var normal = Vector3.Cross(p1 - p0, p2 - p0);
        var length = normal.Length();
        if (length <= 1e-5f)
            return default;

        var direction = normal / length;
        return (context.Lift.OffsetFor(p0, direction),
            context.Lift.OffsetFor(p1, direction),
            context.Lift.OffsetFor(p2, direction));
    }

    /// <summary>The triangle's three export-space corner positions.</summary>
    private static (Vector3 P0, Vector3 P1, Vector3 P2) CornerPositions(
        N64RenderBankFile.N64RenderMesh mesh,
        N64RenderBankFile.N64Triangle triangle,
        int objectIndex,
        EmitContext context)
    {
        var (shell, _, scale, _, _, _, binding) = context;
        return (
            CornerPosition(mesh, triangle.C0, scale,
                CornerOffset(shell, objectIndex, triangle.C0, binding)),
            CornerPosition(mesh, triangle.C1, scale,
                CornerOffset(shell, objectIndex, triangle.C1, binding)),
            CornerPosition(mesh, triangle.C2, scale,
                CornerOffset(shell, objectIndex, triangle.C2, binding)));
    }

    /// <summary>
    ///     Converts one F3DEX2 vertex. Position uses the same handedness map as
    ///     every PS1 export (<c>X, −Y, −Z</c>) so N64 and PS1 conversions of the
    ///     same model land in the same orientation. UVs are S10.5 texels (÷32)
    ///     normalised by the BOUND texture's real dimensions — corpus UV spans
    ///     cluster at 63/127/255, i.e. texel coordinates running 0..N−1 over
    ///     64/128/256-wide sheets, so a fixed divisor is wrong for most faces.
    ///     UVs come from the CORNER, which carries any G_MODIFYVTX override.
    ///     <paramref name="lift" /> is the semi-transparent separation, zero for
    ///     every opaque face.
    /// </summary>
    private static ModelVertex ToVertex(
        N64RenderBankFile.N64RenderMesh mesh,
        N64RenderBankFile.N64Corner corner,
        (int Width, int Height) size,
        int objectIndex,
        EmitContext context,
        Vector3 lift)
    {
        var (shell, _, scale, rig, _, _, binding) = context;
        var offset = CornerOffset(shell, objectIndex, corner, binding);
        var vertex = mesh.Vertices[corner.Vertex];
        var hasNormals = mesh.HasNormals;
        var uScale = 32f * Math.Max(1, size.Width);
        var vScale = 32f * Math.Max(1, size.Height);

        // F3DEX2 reuses the trailing four bytes for either a lit normal or an
        // authored colour, chosen by the group descriptor's G_LIGHTING bit.
        var normal = Vector3.UnitY;
        var colour = Vector4.One;
        if (hasNormals)
        {
            var raw = new Vector3((sbyte)vertex.R, (sbyte)vertex.G, (sbyte)vertex.B) / 127f;
            if (raw.LengthSquared() > 1e-6f)
                normal = Vector3.Normalize(PsxMeshSemantics.ToGltfPosition(raw));

            // Bake the ROM's own rig. Each port uploads exactly ONE Lights1 —
            // a monochrome grey directional plus grey ambient — at startup and
            // never rewrites it, so the shade is ambient + colour*max(0, N.L)
            // and spans grey [70,175] on THPS2/3/SM or [95,215] on THPS1. A lit
            // vertex therefore can never be coloured and can never reach 255,
            // which is why exporting these as pure WHITE was wrong in kind.
            // A degenerate all-zero normal (112 groups corpus-wide, among them
            // THPS1's taxi body and wheels) lands on pure ambient here, which
            // is what the hardware produces for it rather than a chosen
            // fallback. Without a rig we cannot shade, so white stands.
            if (rig != null)
            {
                var shade = rig.Shade(raw);
                colour = new Vector4(shade.X, shade.Y, shade.Z, 1f);
            }
        }
        else
        {
            colour = new Vector4(vertex.R / 255f, vertex.G / 255f, vertex.B / 255f, vertex.A / 255f);
        }

        return new ModelVertex
        {
            Position = PsxMeshSemantics.ToGltfPosition(
                new Vector3(vertex.X * scale, vertex.Y * scale, vertex.Z * scale) + offset) + lift,
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
    private readonly Dictionary<(int Slot, ModelAlphaMode Mode, bool DoubleSided, int Rate), int>
        _materials = [];
    private readonly Dictionary<int, (int Width, int Height)> _sizes = [];

    public int TexturedFaces { get; private set; }
    public int UntexturedFaces { get; private set; }

    public (int MaterialIndex, (int Width, int Height) Size) Resolve(
        N64RenderBankFile.N64Triangle triangle,
        bool translucentVertices)
    {
        var flags = triangle.FaceFlags;
        var semi = (flags & PsxFaceFlags.SemiTransparent) != 0;
        var doubleSided = (flags & PsxFaceFlags.DoubleSided) != 0;
        var rate = semi ? (flags & PsxFaceFlags.BlendRateMask) >> 7 : 0;

        var texture = triangle.TextureSlot > 0 ? textureProvider(triangle.TextureSlot) : null;
        var size = texture != null ? (texture.Width, texture.Height) : (1, 1);
        _sizes[triangle.TextureSlot] = size;

        var (mode, alpha) = ResolveBlendState(rate, semi, translucentVertices, texture);
        var key = (triangle.TextureSlot, mode, doubleSided, rate);
        if (_materials.TryGetValue(key, out var existing))
        {
            Count(triangle.TextureSlot);
            return (existing, size);
        }

        // The ABR suffix advertises a blend equation, and the viewer keys its
        // additive approximation off a terminal __st1/__st3, so only carry it
        // when the material really does blend.
        var blendSuffix = semi && mode == ModelAlphaMode.Blend ? $"__st{rate}" : string.Empty;
        var sideSuffix = doubleSided ? "__2sided" : string.Empty;
        var baseName = texture?.Name ?? "n64_untextured";
        var material = new RenderMaterial
        {
            Name = baseName + blendSuffix + sideSuffix,
            BaseColor = new Vector4(1f, 1f, 1f, alpha),
            DoubleSided = doubleSided,
            // Always unlit, like the PS1 path. The console shades these
            // surfaces diffusely with no specular term at all, whereas glTF's
            // metallic-roughness model always adds a dielectric highlight -
            // which reads as glossiness the game never had. Normals are still
            // exported for consumers that want them.
            Unlit = true,
            AlphaMode = mode,
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

    /// <summary>PS1 ABR rate 0 is 0.5·background + 0.5·face.</summary>
    private const float AverageBlendAlpha = 0.5f;

    /// <summary>
    ///     Works out how a face composites. The PS1 semi-transparent bit alone
    ///     does not decide it, and neither does the art alone.
    ///     <para>
    ///         Rates 1-3 (additive, subtractive, quarter-additive) composite
    ///         with the framebuffer by EQUATION rather than by texel alpha, so
    ///         no alpha content can stand in for them and they always blend.
    ///     </para>
    ///     <para>
    ///         Rate 0 (the 50/50 average) was a PER-TEXEL state on the PS1,
    ///         armed only where the CLUT entry carried the STP marker — and the
    ///         port dropped the marker, so the N64 file can only say "this face
    ///         is an average blend". Which way to read that is settled by a
    ///         Rosetta over every THPS1 level pair, joining on the texture ids
    ///         the ports reuse verbatim:
    ///         <list type="bullet">
    ///             <item>
    ///                 Rate 0 over art with NO alpha channel at all — 2,028
    ///                 triangles — is TRANSLUCENT in the PS1 bake for every
    ///                 single one, none solid. The flag is the only surviving
    ///                 signal that the surface is glass, so it blends at 50%.
    ///                 Downtown's windows are 164 of those triangles: PS1
    ///                 texture 0x015E00C1 bakes 3,249 partial-alpha texels while
    ///                 its N64 copy is 4,096 opaque ones.
    ///             </item>
    ///             <item>
    ///                 Rate 0 over art that DOES carry a transparency key keeps
    ///                 alpha testing. Blanket-blending would make the THPS1
    ///                 medals half-transparent and cost them the depth write
    ///                 that stops their far sheet painting over the near one.
    ///                 This is the rule's known lossy edge: 1,357 level
    ///                 triangles in that cell are translucent on the PS1 side
    ///                 and alpha-test here, keeping their holes and their depth.
    ///             </item>
    ///         </list>
    ///         The control holds the reading up: 93,858 triangles with the bit
    ///         CLEAR are opaque on both sides, against 12 that are not.
    ///     </para>
    /// </summary>
    private static (ModelAlphaMode Mode, float Alpha) ResolveBlendState(
        int blendRate,
        bool semi,
        bool translucentVertices,
        N64ModelCompanions.N64ResolvedTexture? texture)
    {
        if (blendRate != 0 || translucentVertices || texture is { HasGraduatedAlpha: true })
            return (ModelAlphaMode.Blend, 1f);

        if (semi && texture is not { HasCutout: true })
            return (ModelAlphaMode.Blend, AverageBlendAlpha);

        return texture is { HasCutout: true }
            ? (ModelAlphaMode.Mask, 1f)
            : (ModelAlphaMode.Opaque, 1f);
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
