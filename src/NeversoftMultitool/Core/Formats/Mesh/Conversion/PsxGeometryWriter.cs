using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     PSX level/object mesh nodes: per-face colour/UV emission with the
///     semi-transparent decal lift and per-mesh node placement.
/// </summary>
internal static class PsxGeometryWriter
{
    /// <summary>
    ///     Lift applied to ordering-table overlays along their outward normal,
    ///     in glTF units. The PS1 has no depth buffer — transparent shadows and
    ///     opaque decals alike can sit exactly coplanar with their base face and
    ///     win by draw order — but depth-tested glTF viewers z-fight. 0.25 is
    ///     below the minimum level-geometry grid step (1 raw unit / 2.25 ≈
    ///     0.44), so the lift is invisible while clearing depth precision. A
    ///     stacked semi-transparent layer (SKB2's animated waves over its
    ///     static water) lifts a second step (0.5) — still under the grid step.
    /// </summary>
    private const float PsxOverlayFaceLift = 0.25f;

    public static void PopulatePsx(
        ModelDocument document,
        PsxMeshFile psxFile,
        MeshChecksumTextureResolver? textureProvider,
        PshFile? pshFile = null,
        bool flatSkeleton = false,
        IReadOnlySet<int>? flatBoneIndices = null,
        PsxMeshFile? splineClawFile = null,
        MeshChecksumTextureResolver? splineClawTextureProvider = null,
        IReadOnlySet<int>? hiddenObjectIndices = null,
        bool reconstructSplineAppendages = false,
        string nodeNamePrefix = "object",
        PsxGeometryWriterContext? context = null,
        IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>? objectPlacements = null,
        IReadOnlySet<int>? skyObjectIndices = null,
        IReadOnlyDictionary<int, int>? skyLayerOrder = null,
        uint? skyColor = null,
        PsxGhostEmissionOptions? ghostOptions = null)
    {
        if (PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(psxFile))
        {
            PsxSkinnedGeometryWriter.PopulatePsxSkinned(
                document, psxFile, pshFile, textureProvider,
                flatSkeleton, flatBoneIndices, splineClawFile,
                splineClawTextureProvider, hiddenObjectIndices,
                reconstructSplineAppendages, context?.EngineLight);
            ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
            return;
        }

        context ??= new PsxGeometryWriterContext();
        var textureDims = context.TextureDimensions;
        var materialCache = context.Materials;
        var coplanarOverlays = PsxCoplanarOverlayDetector.FindGroups(psxFile);
        var semiTransparentLiftSteps = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(psxFile);
        var semiTransparentLiftDirections = BuildSemiTransparentLiftDirections(psxFile);
        var untexturedMaterial = context.UntexturedMaterialIndex ??=
            ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
            {
                Name = "untextured",
                // Flat PS1 primitives already carry their final display RGB.  A
                // grey material multiplier changes the authored colour a second
                // time, so keep the material neutral.
                BaseColor = Vector4.One,
                DoubleSided = false
            });

        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            var obj = psxFile.Objects[objectIndex];
            if (obj.MeshIndex >= psxFile.Meshes.Count)
                continue;
            if (hiddenObjectIndices?.Contains(objectIndex) == true)
                continue;

            var isSky = skyObjectIndices?.Contains(objectIndex) == true;
            var skyLayerIndex = isSky && skyLayerOrder?.TryGetValue(objectIndex, out var rank) == true
                ? rank
                : 0;
            if (objectPlacements != null)
            {
                if (!objectPlacements.TryGetValue(objectIndex, out var placements))
                    continue;

                // An object whose mesh has ONLY loader-invisible faces normally
                // contributes nothing — but when a TRG entity node places it,
                // the engine's item-flag force draws it as a semi-transparent
                // apparition. Emit those faces as a ghost at the entity
                // placements, behind a default-enabled per-object group.
                IReadOnlyList<PsxFace>? ghostFaces = null;
                var psxMesh = psxFile.Meshes[obj.MeshIndex];
                if (ghostOptions != null
                    && psxMesh.Faces.Count == 0
                    && psxMesh.InvisibleFaces.Count > 0
                    && placements.Any(static placement =>
                        placement.TriggerNodeIndex !=
                        PsxLevelObjectPlacementResolver.BankInstanceNodeIndex))
                {
                    if (!RegisterGhostVisibilityGroup(
                            document, ghostOptions, psxFile, objectIndex,
                            obj.MeshIndex, nodeNamePrefix))
                        continue;

                    ghostFaces = PsxGhostFaces.CreateForcedBlendFaces(psxMesh);
                }

                for (var placementIndex = 0; placementIndex < placements.Count; placementIndex++)
                {
                    var placement = placements[placementIndex];
                    // The engine only draws the forced-blend apparition at its
                    // entity (TRG node) placements; the bank's own env copy
                    // stays invisible.
                    if (ghostFaces != null
                        && placement.TriggerNodeIndex ==
                        PsxLevelObjectPlacementResolver.BankInstanceNodeIndex)
                    {
                        continue;
                    }

                    // The bank's own instance (and a sole placement of any kind)
                    // keeps the plain object name; trigger re-instances carry
                    // their node index so repeats stay distinguishable.
                    var nodeName = placements.Count == 1
                                   || placement.TriggerNodeIndex ==
                                   PsxLevelObjectPlacementResolver.BankInstanceNodeIndex
                        ? $"{nodeNamePrefix}_{objectIndex:D3}"
                        : $"{nodeNamePrefix}_{objectIndex:D3}_node_{placement.TriggerNodeIndex:D3}";
                    PopulatePsxMeshNode(
                        document,
                        psxFile,
                        objectIndex,
                        obj.MeshIndex,
                        nodeName,
                        placement.Transform,
                        materialCache,
                        textureDims,
                        untexturedMaterial,
                        textureProvider,
                        coplanarOverlays,
                        semiTransparentLiftSteps,
                        semiTransparentLiftDirections,
                        context.EngineLight,
                        isSky,
                        skyColor,
                        ghostFaces,
                        skyLayerIndex);
                }
            }
            else
            {
                var transform = Matrix4x4.CreateTranslation(
                    PsxMeshSemantics.ToGltfPosition(PsxMeshSemantics.GetObjectOffset(psxFile, obj)));
                PopulatePsxMeshNode(
                    document,
                    psxFile,
                    objectIndex,
                    obj.MeshIndex,
                    $"{nodeNamePrefix}_{objectIndex:D3}",
                    transform,
                    materialCache,
                    textureDims,
                    untexturedMaterial,
                    textureProvider,
                    coplanarOverlays,
                    semiTransparentLiftSteps,
                    semiTransparentLiftDirections,
                    context.EngineLight,
                    isSky,
                    skyColor,
                    skyLayerIndex: skyLayerIndex);
            }
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static void PopulatePsxMeshNode(
        ModelDocument document,
        PsxMeshFile psxFile,
        int objectIndex,
        int meshIndex,
        string nodeName,
        Matrix4x4 transform,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int> materialCache,
        Dictionary<uint, (int Width, int Height)> textureDims,
        int untexturedMaterial,
        MeshChecksumTextureResolver? textureProvider,
        IReadOnlyDictionary<PsxFaceInstanceKey, PsxCoplanarOverlayAssignment> coplanarOverlays,
        IReadOnlyDictionary<PsxFaceInstanceKey, int> semiTransparentLiftSteps,
        IReadOnlyDictionary<(int X, int Y, int Z), Vector3>? semiTransparentLiftDirections,
        PsxEngineLight? engineLight = null,
        bool isSky = false,
        uint? skyColor = null,
        IReadOnlyList<PsxFace>? ghostFaces = null,
        int skyLayerIndex = 0)
    {
        var psxMesh = psxFile.Meshes[meshIndex];
        var emittedFaces = ghostFaces ?? (IReadOnlyList<PsxFace>)psxMesh.Faces;
        if (emittedFaces.Count == 0)
            return;

        // Detected opaque coplanar overlays (decals authored directly on a
        // larger face) split into their own per-plane-group mesh: the PS1 wins
        // these by ordering-table draw order, so the overlay mesh carries
        // draw-order metadata (viewer renderOrder + Blender object offset)
        // while every vertex stays at its AUTHORED position. The pre-split
        // writer instead lifted overlay faces 0.25 units along their normal
        // inside the shared mesh, corrupting the geometry.
        // Sky domes carry a dual tag: a "sky__" prefix on the mesh AND node
        // name (the mesh name is what survives into the GLB / three.js object
        // names) plus PsxSkyRenderMetadata for the extras channel.
        var meshName = PsxGeometryHelpers.ResolvePsxMeshName(psxFile, meshIndex);
        if (isSky)
        {
            meshName = $"sky__{meshName}";
            nodeName = $"sky__{nodeName}";
        }

        if (ghostFaces != null)
            meshName = $"{meshName}__ghost";

        var indexedFaces = emittedFaces
            .Select((face, faceIndex) => (Face: face, FaceIndex: faceIndex))
            .ToLookup(item =>
                coplanarOverlays.TryGetValue(new PsxFaceInstanceKey(objectIndex, item.FaceIndex), out var assignment)
                    ? (assignment.GroupId, assignment.DrawRank)
                    : (GroupId: -1, DrawRank: 0));

        var liftContext = new PsxMeshEmissionContext(
            objectIndex,
            semiTransparentLiftSteps,
            semiTransparentLiftDirections,
            PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(psxFile, psxFile.Objects[objectIndex])),
            PsxNormalWelder.Build(psxMesh),
            PsxSpriteVertexResolver.TryCreate(psxMesh),
            engineLight);

        // Semi-transparent faces split out of the shared mesh into per-face
        // nodes re-based on their own centroid (see EmitSemiTransparentFaceNodes)
        // — EXCEPT sky layers (drawn in the dedicated depth-cleared sky scene,
        // which has its own compositing), ghost apparitions, and sprite
        // billboard meshes (the viewer rotates the NODE about a shared anchor
        // axis; per-face nodes would scatter the quads).
        var splitSemiTransparent = !isSky
                                   && ghostFaces == null
                                   && liftContext.SpriteResolver?.BillboardMetadata == null;
        IEnumerable<(PsxFace Face, int FaceIndex)> baseFaces = indexedFaces[(-1, 0)];
        List<(PsxFace Face, int FaceIndex)>? semiTransparentFaces = null;
        if (splitSemiTransparent)
        {
            semiTransparentFaces = baseFaces
                .Where(static item => item.Face.IsSemiTransparent)
                .ToList();
            if (semiTransparentFaces.Count == 0)
                semiTransparentFaces = null;
            else
                baseFaces = baseFaces.Where(static item => !item.Face.IsSemiTransparent);
        }

        var mesh = BuildPsxFaceMesh(
            document, psxFile, psxMesh, meshName,
            baseFaces, materialCache, textureDims, untexturedMaterial, textureProvider,
            liftContext);
        if (mesh != null)
        {
            if (isSky)
            {
                foreach (var primitive in mesh.Primitives)
                    primitive.NativeMetadata.Add(new PsxSkyRenderMetadata(skyColor, skyLayerIndex));
            }

            ApplyAxialBillboardMetadata(mesh, liftContext.SpriteResolver);
            ModelDocumentGeometryAdapter.AddMeshNode(document, nodeName, mesh, transform);
        }

        // One mesh per (group, rank): mutually overlapping flagged faces carry
        // distinct ranks, so each rank needs its own DrawIndex and a stacked
        // separation offset (rank x one lift step). Rank 1 keeps the plain
        // __overlayNN name the viewer/Blender already know; deeper ranks add
        // an _rN suffix.
        foreach (var overlayGroup in indexedFaces.Where(static group => group.Key.GroupId >= 0)
                     .OrderBy(static group => group.Key))
        {
            var (groupId, drawRank) = overlayGroup.Key;
            var suffix = drawRank <= 1
                ? $"__overlay{groupId:D2}"
                : $"__overlay{groupId:D2}_r{drawRank}";
            var overlayMesh = BuildPsxFaceMesh(
                document, psxFile, psxMesh, meshName + suffix,
                overlayGroup, materialCache, textureDims, untexturedMaterial, textureProvider,
                liftContext);
            if (overlayMesh == null)
                continue;

            var offset = ComputePsxOverlayLiftVector(
                psxFile.Version, psxMesh, overlayGroup, liftContext.SpriteResolver) * drawRank;
            foreach (var primitive in overlayMesh.Primitives)
            {
                primitive.NativeMetadata.Add(new MeshDrawOrderMetadata(
                    drawRank, drawRank, groupId, offset.X, offset.Y, offset.Z));
            }

            ApplyAxialBillboardMetadata(overlayMesh, liftContext.SpriteResolver);
            ModelDocumentGeometryAdapter.AddMeshNode(
                document, nodeName + suffix, overlayMesh, transform);
        }

        if (semiTransparentFaces != null)
        {
            EmitSemiTransparentFaceNodes(
                document, psxFile, psxMesh, meshName, nodeName, transform,
                semiTransparentFaces, materialCache, textureDims,
                untexturedMaterial, textureProvider, liftContext);
        }
    }

    /// <summary>
    ///     Semi-transparent faces export at PRIMITIVE granularity — the PS1's
    ///     ordering-table unit (gte_avsz4 per poly → addPrim) — one mesh/node
    ///     per face, with the node translated to the face centroid and the
    ///     vertices re-based by its negative. three.js sorts transparent
    ///     objects by ONE projected depth per OBJECT taken from the node
    ///     ORIGIN; the old shared-mesh export left 51/70 of skmall's BLEND
    ///     primitives with an origin outside their own AABB, so the atrium
    ///     glass rail drew both in front of AND behind the fountain sheets in
    ///     a single frame (measured 21% of visible transparent pairs
    ///     mis-ordered; per-face origins 0-2.3%). Re-basing happens strictly
    ///     AFTER BuildPsxFaceMesh applies the semi-transparent lift, whose
    ///     direction map keys on quantised authored-world positions.
    ///     Faces emit in REVERSE authored order: at equal depth three.js
    ///     breaks ties by ascending object id (first-created draws first)
    ///     while the PS1 bucket PREPENDS (first-inserted paints LAST), so the
    ///     earliest authored face must be the last-created node. No static
    ///     draw-order metadata is attached — a fixed rank cannot reproduce a
    ///     view-dependent sort.
    /// </summary>
    private static void EmitSemiTransparentFaceNodes(
        ModelDocument document,
        PsxMeshFile psxFile,
        PsxMesh psxMesh,
        string meshName,
        string nodeName,
        Matrix4x4 transform,
        List<(PsxFace Face, int FaceIndex)> semiTransparentFaces,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int> materialCache,
        Dictionary<uint, (int Width, int Height)> textureDims,
        int untexturedMaterial,
        MeshChecksumTextureResolver? textureProvider,
        PsxMeshEmissionContext liftContext)
    {
        for (var i = semiTransparentFaces.Count - 1; i >= 0; i--)
        {
            var item = semiTransparentFaces[i];
            var faceMesh = BuildPsxFaceMesh(
                document, psxFile, psxMesh, $"{meshName}__blend{item.FaceIndex:D3}",
                [item], materialCache, textureDims, untexturedMaterial, textureProvider,
                liftContext);
            if (faceMesh == null)
                continue;

            var centroid = RebaseMeshToCentroid(faceMesh);
            ModelDocumentGeometryAdapter.AddMeshNode(
                document,
                $"{nodeName}__blend{item.FaceIndex:D3}",
                faceMesh,
                Matrix4x4.CreateTranslation(centroid) * transform);
        }
    }

    /// <summary>
    ///     Moves a mesh's vertex-position average into the node by re-basing
    ///     every vertex on it; returns the centroid (mesh-local, post-lift).
    ///     World geometry is unchanged — only the node origin (three.js's
    ///     transparent sort key) moves onto the geometry.
    /// </summary>
    private static Vector3 RebaseMeshToCentroid(ModelMesh mesh)
    {
        var sum = Vector3.Zero;
        var count = 0;
        foreach (var vertex in mesh.Primitives.SelectMany(static primitive => primitive.Vertices))
        {
            sum += vertex.Position;
            count++;
        }

        if (count == 0)
            return Vector3.Zero;

        var centroid = sum / count;
        foreach (var vertices in mesh.Primitives.Select(static primitive => primitive.Vertices))
        {
            for (var i = 0; i < vertices.Length; i++)
                vertices[i] = vertices[i] with { Position = vertices[i].Position - centroid };
        }

        return centroid;
    }

    /// <summary>
    ///     Tags every primitive of a sprite mesh with its rotation-axis
    ///     descriptor so live consumers (viewer, Blender) can re-face the baked
    ///     quad per frame. No-op for normal meshes and for the (corpus-absent)
    ///     case of sprite quads without one shared axis line.
    /// </summary>
    private static void ApplyAxialBillboardMetadata(
        ModelMesh mesh, PsxSpriteVertexResolver? spriteResolver)
    {
        if (spriteResolver?.BillboardMetadata is not { } billboard)
            return;

        foreach (var primitive in mesh.Primitives)
            primitive.NativeMetadata.Add(billboard);
    }

    /// <summary>
    ///     Appends the per-object visibility group for a ghost-emitted object
    ///     and returns the selected state. Labelled with the resolved mesh name
    ///     when the hash resolves, otherwise the object's node name tagged as a
    ///     hidden apparition.
    ///
    ///     DEFAULT-OFF (2026-08-02). The apparition is a transient runtime state,
    ///     not how the level looks: these meshes are the invisible (bit7) class,
    ///     and the entities placing them are triggers. l1a2's Watcher head
    ///     (0xD7833D12) is placed by BADDY/PLATFORM node 100 whose entire script
    ///     is V_MODEL_CHECKSUM, C_WAIT_FOR_COLLISION, C_SEND_PULSE_TO_LINKS_B,
    ///     C_DIE_QUIETLY — it waits to be touched and then removes itself. The
    ///     forced-blend apparition is real (item flag 0x800), so the geometry is
    ///     still exported and one checkbox restores it, but showing it by default
    ///     misrepresents the level (user-verified: l1a2 reads correctly with the
    ///     group off and wrong with it on).
    /// </summary>
    private static bool RegisterGhostVisibilityGroup(
        ModelDocument document,
        PsxGhostEmissionOptions options,
        PsxMeshFile psxFile,
        int objectIndex,
        int meshIndex,
        string nodeNamePrefix)
    {
        var id = $"psx.ghost.{options.AssetHash:X8}.{objectIndex:D3}";
        var enabled = options.VisibilityOverrides != null
                      && options.VisibilityOverrides.TryGetValue(id, out var selected)
                      && selected;
        var nameHash = meshIndex < psxFile.MeshNameHashes.Length
            ? psxFile.MeshNameHashes[meshIndex]
            : 0u;
        document.VisibilityGroups.Add(new ModelVisibilityGroup
        {
            Id = id,
            Label = QbKey.QbKey.TryResolve(nameHash)
                    ?? $"{nodeNamePrefix}_{objectIndex:D3} (hidden apparition)",
            DefaultEnabled = false,
            IsEnabled = enabled,
            Source = ModelVisibilityGroupSource.HiddenApparition,
            SourceReference =
                $"invisible-class mesh {meshIndex} (hash 0x{nameHash:X8}) " +
                "forced blended at its TRG entity placement"
        });
        return enabled;
    }

    private static ModelMesh? BuildPsxFaceMesh(
        ModelDocument document,
        PsxMeshFile psxFile,
        PsxMesh psxMesh,
        string meshName,
        IEnumerable<(PsxFace Face, int FaceIndex)> faces,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int> materialCache,
        Dictionary<uint, (int Width, int Height)> textureDims,
        int untexturedMaterial,
        MeshChecksumTextureResolver? textureProvider,
        PsxMeshEmissionContext liftContext)
    {
        var mesh = new ModelMesh { Name = meshName };
        foreach (var group in faces.GroupBy(static item =>
                     PsxGeometryHelpers.GetPsxMaterialKey(item.Face)))
        {
            var materialIndex = group.Key.Hash == 0 &&
                                !group.Key.SemiTransparent &&
                                !group.Key.DoubleSided
                ? untexturedMaterial
                : PsxGeometryHelpers.GetOrCreatePsxMaterial(
                    document,
                    group.Key.Hash,
                    group.Key.SemiTransparent,
                    group.Key.DoubleSided,
                    group.Key.BlendRate,
                    textureProvider,
                    textureDims,
                    materialCache);

            var texDims = group.Key.Hash != 0 && textureDims.TryGetValue(group.Key.Hash, out var dims)
                ? dims
                : (Width: 256, Height: 256);
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var item in group)
                AddPsxFace(
                    vertices,
                    indices,
                    psxFile.Version,
                    psxMesh,
                    item.Face,
                    psxFile.GouraudPalette,
                    texDims,
                    liftContext.LiftPlanFor(item.FaceIndex),
                    liftContext.NormalWelder,
                    liftContext.EngineLight,
                    liftContext.SpriteResolver);

            ModelDocumentGeometryAdapter.AddPrimitive(mesh, $"mat_{materialIndex:D3}", materialIndex, vertices,
                indices);
        }

        return mesh.Primitives.Count > 0 ? mesh : null;
    }

    /// <summary>
    ///     The separation vector the Blender importer applies at object level to
    ///     an overlay group — the old baked lift's direction (outward geometric
    ///     normal of the first emitted face; the group is coplanar by
    ///     construction) times the old lift magnitude.
    /// </summary>
    private static Vector3 ComputePsxOverlayLiftVector(
        ushort version,
        PsxMesh psxMesh,
        IEnumerable<(PsxFace Face, int FaceIndex)> faces,
        PsxSpriteVertexResolver? spriteResolver = null)
    {
        foreach (var (face, _) in faces)
        {
            var v0 = MakePsxVertex(version, psxMesh, face, 0, Vector4.One, null, (256, 256),
                spriteResolver: spriteResolver);
            var v1 = MakePsxVertex(version, psxMesh, face, 1, Vector4.One, null, (256, 256),
                spriteResolver: spriteResolver);
            var v2 = MakePsxVertex(version, psxMesh, face, 2, Vector4.One, null, (256, 256),
                spriteResolver: spriteResolver);
            var geometricNormal = Vector3.Cross(
                v2.Position - v0.Position, v1.Position - v0.Position);
            var lengthSquared = geometricNormal.LengthSquared();
            if (lengthSquared <= 1e-12f)
                continue;

            return geometricNormal / MathF.Sqrt(lengthSquared) * PsxOverlayFaceLift;
        }

        return Vector3.Zero;
    }

    private static void AddPsxFace(
        List<ModelVertex> vertices,
        List<int> indices,
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        Vector4[]? gouraudPalette,
        (int Width, int Height) texDims,
        PsxFaceLiftPlan liftPlan,
        PsxNormalWelder? normalWelder,
        PsxEngineLight? engineLight,
        PsxSpriteVertexResolver? spriteResolver = null)
    {
        var isPs1 = version != 0x06;
        // PS1 lit faces on the NON-character path bake the engine light:
        // authored albedo × FE light per corner normal (the engine's exact
        // DPCL multiply — control.psx's dark thumbstick albedo top-lit grey).
        // The packet colours carry the baked result so the PS1-fidelity path
        // draws it verbatim. v6 keeps the neutral rule (the PC renderer's
        // dynamic path is approximated by viewer lighting there).
        // Per FACE, matching the engine: the PS1 loader ORs each face's word0
        // into SModel.Flags so the model-level bit is just "any face is lit",
        // and ProcessPolys then selects the lit primitive variant per face.
        var bakeEngineLight = isPs1 && engineLight != null
                              && PsxGeometryHelpers.IsEngineLitFace(version, mesh, face);
        var (c0, c1, c2, c3) = PsxGeometryHelpers.ComputePsxFaceColors(
            version, mesh, face, gouraudPalette, neutralizeLitFaces: !bakeEngineLight);
        c0 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c0);
        c1 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c1);
        c2 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c2);
        c3 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c3);
        if (bakeEngineLight)
        {
            c0 = PsxGeometryHelpers.BakeEngineLight(mesh, face, 0, engineLight!);
            c1 = PsxGeometryHelpers.BakeEngineLight(mesh, face, 1, engineLight!);
            c2 = PsxGeometryHelpers.BakeEngineLight(mesh, face, 2, engineLight!);
            if (face.IsQuad)
                c3 = PsxGeometryHelpers.BakeEngineLight(mesh, face, 3, engineLight!);
        }

        var isPs1TexturedModulation = isPs1 && face.IsTextured;
        // Engine-lit faces of MIXED-lit files skip the packet colour so the
        // viewer's standard lit path shades them from normals; baked FE-prop
        // faces carry their baked colours in the packets (mirrors
        // PsxSkinnedGeometryWriter and the IsEngineLitFace contract — emitting
        // neutralized packets drew lit faces flat in PS1-fidelity mode).
        // Always emit the PS1 packet. Gating it on the lit state (added
        // 2026-07-29) made an all-lit file emit NO packet at all, which
        // downgrades the vertex type and strips both _PSX_COLOR_0 and
        // _PSX_FLAGS_0 from the GLB — silently switching off the viewer's
        // PS1-fidelity path for exactly the files it matters most on.
        var emitPacket = isPs1;
        // A semi-transparent zero-hash primitive has just been converted into
        // an untextured display proxy. Every other textured PS1 colour is still
        // in the native 128-neutral modulation domain at this point.
        var packetUsesTexturedScale = face.IsTextured &&
                                      (face.TextureHash != 0 || !face.IsSemiTransparent);
        Vector4? p0 = emitPacket
            ? PsxGeometryHelpers.ToPsxPacketColor(c0, packetUsesTexturedScale)
            : null;
        Vector4? p1 = emitPacket
            ? PsxGeometryHelpers.ToPsxPacketColor(c1, packetUsesTexturedScale)
            : null;
        Vector4? p2 = emitPacket
            ? PsxGeometryHelpers.ToPsxPacketColor(c2, packetUsesTexturedScale)
            : null;
        Vector4? p3 = emitPacket
            ? PsxGeometryHelpers.ToPsxPacketColor(c3, packetUsesTexturedScale)
            : null;
        c0 = PsxGeometryHelpers.DisplayRgbToLinear(c0, isPs1TexturedModulation);
        c1 = PsxGeometryHelpers.DisplayRgbToLinear(c1, isPs1TexturedModulation);
        c2 = PsxGeometryHelpers.DisplayRgbToLinear(c2, isPs1TexturedModulation);
        c3 = PsxGeometryHelpers.DisplayRgbToLinear(c3, isPs1TexturedModulation);
        var v0 = MakePsxVertex(version, mesh, face, 0, c0, p0, texDims, normalWelder, spriteResolver);
        var v1 = MakePsxVertex(version, mesh, face, 1, c1, p1, texDims, normalWelder, spriteResolver);
        var v2 = MakePsxVertex(version, mesh, face, 2, c2, p2, texDims, normalWelder, spriteResolver);
        var v3 = face.IsQuad
            ? MakePsxVertex(version, mesh, face, 3, c3, p3, texDims, normalWelder, spriteResolver)
            : default;

        if (face.IsSemiTransparent)
        {
            // Geometric normal of the emitted CCW triangle (v0, v2, v1) —
            // outward per the winding convention below — so semi-transparent
            // shadows/glass lift away from the surface they overlay. Detected
            // OPAQUE coplanar overlays no longer lift: they split into their
            // own draw-order-metadata mesh (see PopulatePsxMeshNode). Each
            // corner lifts along the POSITION-AVERAGED normal of the mesh's
            // semi-transparent faces, not this face's own normal: a per-face
            // direction tears curved connected surfaces apart at shared edges
            // (Spider-Man's all-semi-transparent webdome cracked open at every
            // seam), while averaged directions move shared positions together
            // and reduce to the face normal on flat decals. A stacked animated
            // layer over a static one lifts extra steps (FindSemiTransparentLayerSteps).
            var geometricNormal = Vector3.Cross(
                v2.Position - v0.Position, v1.Position - v0.Position);
            var lengthSquared = geometricNormal.LengthSquared();
            if (lengthSquared > 1e-12f)
            {
                var direction = geometricNormal / MathF.Sqrt(lengthSquared);
                var magnitude = PsxOverlayFaceLift * Math.Max(liftPlan.Steps, 1);
                var directions = liftPlan.Directions;
                var offset = liftPlan.AuthoredOffset;
                v0 = LiftVertex(v0, direction, magnitude, directions, offset);
                v1 = LiftVertex(v1, direction, magnitude, directions, offset);
                v2 = LiftVertex(v2, direction, magnitude, directions, offset);
                if (face.IsQuad)
                    v3 = LiftVertex(v3, direction, magnitude, directions, offset);
            }
        }

        // glTF front faces are CCW; PSX slot order is CW under the (X,-Y,-Z)
        // handedness map, so emit reversed to make winding agree with the
        // stored (outward) normals. Probe: psx_lod_part_probe.py --normals.
        ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, v0, v2, v1);

        if (face.IsQuad)
            ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, v1, v2, v3);
    }

    /// <summary>
    ///     Per-face inputs for the semi-transparent lift: how many steps this
    ///     face rises, the file's position-averaged directions, and the object
    ///     offset that keys into them.
    /// </summary>
    private readonly record struct PsxFaceLiftPlan(
        int Steps,
        IReadOnlyDictionary<(int X, int Y, int Z), Vector3>? Directions,
        Vector3 AuthoredOffset);

    private static ModelVertex LiftVertex(
        ModelVertex vertex,
        Vector3 faceDirection,
        float magnitude,
        IReadOnlyDictionary<(int X, int Y, int Z), Vector3>? liftDirections,
        Vector3 authoredOffset)
    {
        var direction = liftDirections != null &&
                        liftDirections.TryGetValue(
                            QuantizeLiftPosition(vertex.Position + authoredOffset), out var averaged)
            ? averaged
            : faceDirection;
        return vertex with { Position = vertex.Position + direction * magnitude };
    }

    /// <summary>
    ///     Position-keyed average of the outward geometric normals of the
    ///     FILE's semi-transparent faces, in authored-world space, so
    ///     coincident corners of adjacent lifted faces translate together
    ///     instead of tearing along shared edges — including corners shared
    ///     BETWEEN objects (webdome3/firedome build their domes from one ring
    ///     object per band; a per-mesh map left a horizontal crack at every
    ///     band boundary). Positions quantize to 1/64 unit — far below the
    ///     ~0.44 authoring grid step, so only truly coincident corners merge.
    ///     An average that cancels to zero (opposing sheets sharing an edge)
    ///     falls back to the per-face direction at lift time.
    /// </summary>
    private static Dictionary<(int X, int Y, int Z), Vector3>? BuildSemiTransparentLiftDirections(
        PsxMeshFile psxFile)
    {
        Dictionary<(int X, int Y, int Z), Vector3>? sums = null;
        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            var obj = psxFile.Objects[objectIndex];
            if (obj.MeshIndex >= psxFile.Meshes.Count)
                continue;

            var mesh = psxFile.Meshes[obj.MeshIndex];
            var offset = PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(psxFile, obj));
            for (var faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
            {
                var face = mesh.Faces[faceIndex];
                if (!face.IsSemiTransparent)
                    continue;

                var count = face.IsQuad ? 4 : 3;
                var points = new Vector3[count];
                var valid = true;
                for (var slot = 0; slot < count; slot++)
                {
                    var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
                    if (vertexIndex >= mesh.Vertices.Count)
                    {
                        valid = false;
                        break;
                    }

                    var vertex = mesh.Vertices[(int)vertexIndex];
                    points[slot] = new Vector3(vertex.X, -vertex.Y, -vertex.Z) + offset;
                }

                if (!valid)
                    continue;

                // Same winding as the emitted (v0, v2, v1) triangle in AddPsxFace.
                var normal = Vector3.Cross(points[2] - points[0], points[1] - points[0]);
                var lengthSquared = normal.LengthSquared();
                if (lengthSquared <= 1e-12f)
                    continue;

                normal /= MathF.Sqrt(lengthSquared);
                sums ??= [];
                for (var slot = 0; slot < count; slot++)
                {
                    var key = QuantizeLiftPosition(points[slot]);
                    sums[key] = sums.TryGetValue(key, out var sum) ? sum + normal : normal;
                }
            }
        }

        if (sums == null)
            return null;

        Dictionary<(int X, int Y, int Z), Vector3>? directions = null;
        foreach (var (key, sum) in sums)
        {
            var lengthSquared = sum.LengthSquared();
            if (lengthSquared <= 1e-12f)
                continue;

            directions ??= [];
            directions[key] = sum / MathF.Sqrt(lengthSquared);
        }

        return directions;
    }

    private static (int X, int Y, int Z) QuantizeLiftPosition(Vector3 position)
    {
        return ((int)MathF.Round(position.X * 64f),
            (int)MathF.Round(position.Y * 64f),
            (int)MathF.Round(position.Z * 64f));
    }

    /// <summary>
    ///     Per-node bundle of face-emission inputs: which semi-transparent
    ///     faces get extra stacked-layer lift steps (keyed by this node's
    ///     object index), the file's authored-world-space position-averaged
    ///     lift directions plus this object's authored offset to key into
    ///     them, and the normal welder for meshes without per-vertex normals.
    /// </summary>
    private sealed class PsxMeshEmissionContext(
        int objectIndex,
        IReadOnlyDictionary<PsxFaceInstanceKey, int> stepsByFace,
        IReadOnlyDictionary<(int X, int Y, int Z), Vector3>? directions,
        Vector3 authoredOffset,
        PsxNormalWelder? normalWelder,
        PsxSpriteVertexResolver? spriteResolver,
        PsxEngineLight? engineLight = null)
    {
        internal PsxEngineLight? EngineLight => engineLight;

        /// <summary>Everything the lift needs for one face.</summary>
        internal PsxFaceLiftPlan LiftPlanFor(int faceIndex)
        {
            return new PsxFaceLiftPlan(LiftStepsFor(faceIndex), directions, authoredOffset);
        }

        internal PsxNormalWelder? NormalWelder => normalWelder;

        internal PsxSpriteVertexResolver? SpriteResolver => spriteResolver;

        internal int LiftStepsFor(int faceIndex)
        {
            return stepsByFace.TryGetValue(new PsxFaceInstanceKey(objectIndex, faceIndex), out var steps)
                ? steps
                : 1;
        }
    }

    private static ModelVertex MakePsxVertex(
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        int slot,
        Vector4 color,
        Vector4? psxPacketColor,
        (int Width, int Height) texDims,
        PsxNormalWelder? normalWelder = null,
        PsxSpriteVertexResolver? spriteResolver = null)
    {
        var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
        var psxPrimitiveFlags = Vector3.Zero;
        if (psxPacketColor.HasValue)
        {
            var texturedFlag = face.IsTextured &&
                               (face.TextureHash != 0 || !face.IsSemiTransparent)
                ? 1f
                : 0f;
            var gouraudFlag = face.IsGouraud ? 1f : 0f;
            psxPrimitiveFlags = new Vector3(texturedFlag, gouraudFlag, 1f);
        }

        if (vertexIndex >= mesh.Vertices.Count)
        {
            return new ModelVertex(Vector3.Zero, Vector3.UnitY, color, Vector2.Zero)
            {
                PsxPacketColor = psxPacketColor,
                PsxPrimitiveFlags = psxPrimitiveFlags
            };
        }

        var nativeVertex = mesh.Vertices[(int)vertexIndex];
        var texCoord = face.GetTextureCoordinate(slot);
        // Sprite vertices (type bit4) store anchor/mate byte offsets, not a
        // position — resolve the billboard corner instead of emitting the raw
        // fields as coordinates (the "thin sliver" leaves/antennas bug).
        var position = spriteResolver != null
                       && spriteResolver.TryResolvePosition(vertexIndex, out var spriteCorner)
            ? spriteCorner
            : new Vector3(nativeVertex.X, -nativeVertex.Y, -nativeVertex.Z);
        var normal = PsxGeometryHelpers.ComputePsxVertexNormal(mesh, face, vertexIndex);
        if (normalWelder != null)
            normal = normalWelder.Resolve(position, normal);
        return new ModelVertex(
            position,
            normal,
            color,
            PsxGeometryHelpers.ComputePsxTextureUv(version, face, texCoord.U, texCoord.V, texDims.Width,
                texDims.Height))
        {
            PsxPacketColor = psxPacketColor,
            PsxPrimitiveFlags = psxPrimitiveFlags,
            TextureWibble = ModelTextureWibble.FromFace(version, face, slot, texDims)
        };
    }

    internal sealed class PsxGeometryWriterContext
    {
        // Level geometry and object regions are loaded into one runtime hash
        // namespace. Share their material cache so an identical native
        // texture/render-state tuple remains one material in the assembled
        // document, just as it is in the game.
        internal Dictionary<uint, (int Width, int Height)> TextureDimensions { get; } = [];

        internal Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>
            Materials { get; } = [];

        internal int? UntexturedMaterialIndex { get; set; }

        /// <summary>
        ///     Which engine light rig to bake into engine-lit faces, or null to
        ///     leave them to the viewer's own lighting (the default).
        ///     OFF by default: the converter cannot tell an FE prop from an
        ///     in-level character. The engine's own gate is a MODEL flag, but
        ///     disc headers ship 0x8 across lit characters and FE props alike
        ///     (decomp-verified, see PsxMesh.UsesDynamicLighting), and the
        ///     fallback signal "every face is lit" misclassifies the main
        ///     characters — spidey 372/372 and blackcat 338/338 are fully lit,
        ///     so they took the prop branch and shipped with a front-end light
        ///     baked into their in-level vertex colours. Until a signal exists
        ///     that actually separates the two, this stays caller-controlled.
        /// </summary>
        internal PsxEngineLight? EngineLight { get; init; }
    }
}
