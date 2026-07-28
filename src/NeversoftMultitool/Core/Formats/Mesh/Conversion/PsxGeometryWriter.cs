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
    ///     0.44), so the lift is invisible while clearing depth precision.
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
        IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>? objectPlacements = null)
    {
        if (PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(psxFile))
        {
            PsxSkinnedGeometryWriter.PopulatePsxSkinned(
                document, psxFile, pshFile, textureProvider,
                flatSkeleton, flatBoneIndices, splineClawFile,
                splineClawTextureProvider, hiddenObjectIndices,
                reconstructSplineAppendages);
            ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
            return;
        }

        context ??= new PsxGeometryWriterContext();
        var textureDims = context.TextureDimensions;
        var materialCache = context.Materials;
        var coplanarOverlays = PsxCoplanarOverlayDetector.FindGroups(psxFile);
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

            if (objectPlacements != null)
            {
                if (!objectPlacements.TryGetValue(objectIndex, out var placements))
                    continue;

                for (var placementIndex = 0; placementIndex < placements.Count; placementIndex++)
                {
                    var placement = placements[placementIndex];
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
                        coplanarOverlays);
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
                    coplanarOverlays);
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
        IReadOnlyDictionary<PsxFaceInstanceKey, int> coplanarOverlays)
    {
        var psxMesh = psxFile.Meshes[meshIndex];
        if (psxMesh.Faces.Count == 0)
            return;

        // Detected opaque coplanar overlays (decals authored directly on a
        // larger face) split into their own per-plane-group mesh: the PS1 wins
        // these by ordering-table draw order, so the overlay mesh carries
        // draw-order metadata (viewer renderOrder + Blender object offset)
        // while every vertex stays at its AUTHORED position. The pre-split
        // writer instead lifted overlay faces 0.25 units along their normal
        // inside the shared mesh, corrupting the geometry.
        var meshName = PsxGeometryHelpers.ResolvePsxMeshName(psxFile, meshIndex);
        var indexedFaces = psxMesh.Faces
            .Select((face, faceIndex) => (Face: face, FaceIndex: faceIndex))
            .ToLookup(item =>
                coplanarOverlays.TryGetValue(new PsxFaceInstanceKey(objectIndex, item.FaceIndex), out var groupId)
                    ? groupId
                    : -1);

        var mesh = BuildPsxFaceMesh(
            document, psxFile, psxMesh, meshName,
            indexedFaces[-1], materialCache, textureDims, untexturedMaterial, textureProvider);
        if (mesh != null)
            ModelDocumentGeometryAdapter.AddMeshNode(document, nodeName, mesh, transform);

        foreach (var overlayGroup in indexedFaces.Where(static group => group.Key >= 0)
                     .OrderBy(static group => group.Key))
        {
            var overlayMesh = BuildPsxFaceMesh(
                document, psxFile, psxMesh, $"{meshName}__overlay{overlayGroup.Key:D2}",
                overlayGroup, materialCache, textureDims, untexturedMaterial, textureProvider);
            if (overlayMesh == null)
                continue;

            var offset = ComputePsxOverlayLiftVector(psxFile.Version, psxMesh, overlayGroup);
            foreach (var primitive in overlayMesh.Primitives)
            {
                primitive.NativeMetadata.Add(new MeshDrawOrderMetadata(
                    1, 1, overlayGroup.Key, offset.X, offset.Y, offset.Z));
            }

            ModelDocumentGeometryAdapter.AddMeshNode(
                document, $"{nodeName}__overlay{overlayGroup.Key:D2}", overlayMesh, transform);
        }
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
        MeshChecksumTextureResolver? textureProvider)
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
                    texDims);

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
        IEnumerable<(PsxFace Face, int FaceIndex)> faces)
    {
        foreach (var (face, _) in faces)
        {
            var v0 = MakePsxVertex(version, psxMesh, face, 0, Vector4.One, null, (256, 256));
            var v1 = MakePsxVertex(version, psxMesh, face, 1, Vector4.One, null, (256, 256));
            var v2 = MakePsxVertex(version, psxMesh, face, 2, Vector4.One, null, (256, 256));
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
        (int Width, int Height) texDims)
    {
        var (c0, c1, c2, c3) = PsxGeometryHelpers.ComputePsxFaceColors(
            version, mesh, face, gouraudPalette);
        c0 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c0);
        c1 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c1);
        c2 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c2);
        c3 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c3);
        var isPs1 = version != 0x06;
        var isPs1TexturedModulation = isPs1 && face.IsTextured;
        // A semi-transparent zero-hash primitive has just been converted into
        // an untextured display proxy. Every other textured PS1 colour is still
        // in the native 128-neutral modulation domain at this point.
        var packetUsesTexturedScale = face.IsTextured &&
                                      (face.TextureHash != 0 || !face.IsSemiTransparent);
        Vector4? p0 = isPs1
            ? PsxGeometryHelpers.ToPsxPacketColor(c0, packetUsesTexturedScale)
            : null;
        Vector4? p1 = isPs1
            ? PsxGeometryHelpers.ToPsxPacketColor(c1, packetUsesTexturedScale)
            : null;
        Vector4? p2 = isPs1
            ? PsxGeometryHelpers.ToPsxPacketColor(c2, packetUsesTexturedScale)
            : null;
        Vector4? p3 = isPs1
            ? PsxGeometryHelpers.ToPsxPacketColor(c3, packetUsesTexturedScale)
            : null;
        c0 = PsxGeometryHelpers.DisplayRgbToLinear(c0, isPs1TexturedModulation);
        c1 = PsxGeometryHelpers.DisplayRgbToLinear(c1, isPs1TexturedModulation);
        c2 = PsxGeometryHelpers.DisplayRgbToLinear(c2, isPs1TexturedModulation);
        c3 = PsxGeometryHelpers.DisplayRgbToLinear(c3, isPs1TexturedModulation);
        var v0 = MakePsxVertex(version, mesh, face, 0, c0, p0, texDims);
        var v1 = MakePsxVertex(version, mesh, face, 1, c1, p1, texDims);
        var v2 = MakePsxVertex(version, mesh, face, 2, c2, p2, texDims);
        var v3 = face.IsQuad
            ? MakePsxVertex(version, mesh, face, 3, c3, p3, texDims)
            : default;

        if (face.IsSemiTransparent)
        {
            // Geometric normal of the emitted CCW triangle (v0, v2, v1) —
            // outward per the winding convention below — so semi-transparent
            // shadows/glass lift away from the surface they overlay. Detected
            // OPAQUE coplanar overlays no longer lift: they split into their
            // own draw-order-metadata mesh (see PopulatePsxMeshNode). The ST
            // lift stays because it is per-face-normal (non-rigid) and ST-on-ST
            // stack order needs the OT tie-break direction pinned against the
            // decomp before it can become metadata.
            var geometricNormal = Vector3.Cross(
                v2.Position - v0.Position, v1.Position - v0.Position);
            var lengthSquared = geometricNormal.LengthSquared();
            if (lengthSquared > 1e-12f)
            {
                var lift = geometricNormal / MathF.Sqrt(lengthSquared) * PsxOverlayFaceLift;
                v0 = v0 with { Position = v0.Position + lift };
                v1 = v1 with { Position = v1.Position + lift };
                v2 = v2 with { Position = v2.Position + lift };
                if (face.IsQuad)
                    v3 = v3 with { Position = v3.Position + lift };
            }
        }

        // glTF front faces are CCW; PSX slot order is CW under the (X,-Y,-Z)
        // handedness map, so emit reversed to make winding agree with the
        // stored (outward) normals. Probe: psx_lod_part_probe.py --normals.
        ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, v0, v2, v1);

        if (face.IsQuad)
            ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, v1, v2, v3);
    }

    private static ModelVertex MakePsxVertex(
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        int slot,
        Vector4 color,
        Vector4? psxPacketColor,
        (int Width, int Height) texDims)
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
        return new ModelVertex(
            new Vector3(nativeVertex.X, -nativeVertex.Y, -nativeVertex.Z),
            PsxGeometryHelpers.ComputePsxVertexNormal(mesh, face, vertexIndex),
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
    }
}
