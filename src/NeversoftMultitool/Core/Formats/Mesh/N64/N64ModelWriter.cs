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
        bool includeAllAnimations = false,
        bool oneShot = false)
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
                shell, animationPlan.Animations, animationIndices, includeAllAnimations, oneShot)
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
        bool includeAllAnimations,
        bool oneShot)
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
                clips.Add(($"anim_{index}", bank.DecodeSlot(index, shell.Objects.Count, oneShot)));
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
    ///     every opaque face. Normal, colour, and UV each have their own helper
    ///     so they can be pinned without building a synthetic render bank.
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

        return new ModelVertex
        {
            Position = PsxMeshSemantics.ToGltfPosition(
                new Vector3(vertex.X * scale, vertex.Y * scale, vertex.Z * scale) + offset) + lift,
            Normal = ComputeN64Normal(vertex, mesh.HasNormals),
            Color = ComputeN64VertexColour(vertex, mesh.HasNormals, rig),
            // Corner ST, not the pool vertex's: G_MODIFYVTX can rewrite it.
            TexCoord = ComputeN64TextureUv(corner.S, corner.T, size.Width, size.Height)
        };
    }

    /// <summary>
    ///     Half a texel in the S10.5 fixed point the pool stores ST in.
    /// </summary>
    private const float HalfTexelS10_5 = 16f;

    /// <summary>
    ///     Maps a corner's S10.5 texel coordinate onto the bound texture,
    ///     addressing texel CENTRES. Stored spans run 0..N−1 over an N-wide
    ///     sheet, i.e. integer texel INDICES, so sending index <c>k</c> to
    ///     <c>k/N</c> lands on the texel's leading EDGE; a linearly filtered
    ///     sample there blends with the neighbour across the edge, which under
    ///     REPEAT is the opposite side of the sheet. That is the seam. The PS1
    ///     writer already addresses centres for the identical reason — see
    ///     <see cref="PsxGeometryHelpers.ComputePsxTextureUv" /> — and these two
    ///     paths convert the same authored art, so they must agree.
    ///     Coordinates beyond the sheet still exceed 1 and tile naturally.
    /// </summary>
    internal static Vector2 ComputeN64TextureUv(short s, short t, int width, int height)
    {
        return new Vector2(
            (s + HalfTexelS10_5) / (32f * Math.Max(1, width)),
            (t + HalfTexelS10_5) / (32f * Math.Max(1, height)));
    }

    /// <summary>
    ///     F3DEX2 reuses a vertex's trailing four bytes for either a lit normal
    ///     or an authored colour, chosen by the group descriptor's G_LIGHTING
    ///     bit. Only the normal reading produces a normal.
    /// </summary>
    internal static Vector3 ComputeN64Normal(
        N64RenderBankFile.N64Vertex vertex,
        bool hasNormals)
    {
        if (!hasNormals)
            return Vector3.UnitY;

        var raw = SignedNormal(vertex);
        return raw.LengthSquared() > 1e-6f
            ? Vector3.Normalize(PsxMeshSemantics.ToGltfPosition(raw))
            : Vector3.UnitY;
    }

    /// <summary>
    ///     Resolves a vertex's glTF <c>COLOR_0</c>.
    ///     <para>
    ///         For a lit vertex this bakes the ROM's own rig. Each port uploads
    ///         exactly ONE Lights1 — a monochrome grey directional plus grey
    ///         ambient — at startup and never rewrites it, so the shade is
    ///         <c>ambient + colour*max(0, N·L)</c> and spans grey [70,175] on
    ///         THPS2/3/SM or [95,215] on THPS1. A lit vertex can therefore never
    ///         be coloured and never reach 255, which is why exporting these as
    ///         pure WHITE was wrong in kind. A degenerate all-zero normal (112
    ///         groups corpus-wide, among them THPS1's taxi body and wheels)
    ///         lands on pure ambient, which is what the hardware produces for
    ///         it rather than a chosen fallback. Without a rig we cannot shade,
    ///         so white stands.
    ///     </para>
    ///     <para>
    ///         Both readings are DISPLAY-domain values — the RSP does its
    ///         lighting arithmetic in the same 8-bit space the framebuffer
    ///         shows, and an unlit vertex's bytes are emitted verbatim — while
    ///         glTF <c>COLOR_0</c> is a LINEAR multiplier applied to an
    ///         sRGB-decoded texture. Writing the normalized bytes straight
    ///         through gamma-encodes them a second time, which is why every N64
    ///         model read far brighter than the console and than the PS1 export
    ///         of the same asset: ambient 70/255 displayed near 144. The PS1
    ///         writers already convert; the plain sRGB branch is the right one
    ///         here because F3DEX2's combiner neutral is 255, not the PS1
    ///         packet's 128. Alpha is coverage and passes through untouched.
    ///     </para>
    ///     <para>
    ///         <b>The shade really is a multiplier</b>, which is why baking it
    ///         is correct rather than merely self-consistent. The model draw
    ///         path emits exactly two combiners, chosen by kind bit 0
    ///         (Spider-Man @0x800D2154, word1 @0x800D2178):
    ///         <c>0xFC127E05</c> for a textured group, giving cycle 0
    ///         <c>TEXEL0 * SHADE</c>, and <c>0xFC527E1F</c> for an untextured
    ///         one, giving <c>ENVIRONMENT * SHADE</c>. Both share word1
    ///         <c>0xFFFFF2F8</c>, whose b and d slots are 0. A decal combiner
    ///         that would ignore shade does exist in these ROMs
    ///         (<c>0xFCFFFFFF FFFCF279</c>) but only in the 2D blitter, which
    ///         reads no group descriptor. So a dark result on a self-illuminated
    ///         surface — the Human Torch's flame is the reported case — is the
    ///         console's own output, not an export defect.
    ///     </para>
    ///     <para>
    ///         NOT reproduced: cycle 1 is
    ///         <c>COMBINED * ENVIRONMENT + PRIMITIVE</c>, and neither global is
    ///         exported. Both are BSS (<c>0x80105084</c> and <c>0x80105080</c>
    ///         in Spider-Man) and no direct-offset store to either exists in the
    ///         image, so their runtime values are unrecovered. ENVIRONMENT is a
    ///         multiplier and can only darken further; PRIMITIVE is ADDITIVE and
    ///         could brighten. Recovering them is the open residual here.
    ///     </para>
    /// </summary>
    internal static Vector4 ComputeN64VertexColour(
        N64RenderBankFile.N64Vertex vertex,
        bool hasNormals,
        N64LightRig? rig)
    {
        Vector4 colour;
        if (!hasNormals)
        {
            colour = new Vector4(
                vertex.R / 255f, vertex.G / 255f, vertex.B / 255f, vertex.A / 255f);
        }
        else if (rig != null)
        {
            var shade = rig.Shade(SignedNormal(vertex));
            colour = new Vector4(shade.X, shade.Y, shade.Z, 1f);
        }
        else
        {
            colour = Vector4.One;
        }

        return PsxGeometryHelpers.DisplayRgbToLinear(colour);
    }

    private static Vector3 SignedNormal(N64RenderBankFile.N64Vertex vertex)
    {
        return new Vector3((sbyte)vertex.R, (sbyte)vertex.G, (sbyte)vertex.B) / 127f;
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
