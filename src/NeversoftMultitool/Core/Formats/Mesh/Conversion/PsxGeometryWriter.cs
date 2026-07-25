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
        var coplanarOverlays = PsxCoplanarOverlayDetector.Find(psxFile);
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
        IReadOnlySet<PsxFaceInstanceKey> coplanarOverlays)
    {
        var psxMesh = psxFile.Meshes[meshIndex];
        if (psxMesh.Faces.Count == 0)
            return;

        var mesh = new ModelMesh { Name = PsxGeometryHelpers.ResolvePsxMeshName(psxFile, meshIndex) };
        var indexedFaces = psxMesh.Faces.Select((face, faceIndex) => (Face: face, FaceIndex: faceIndex));
        foreach (var group in indexedFaces.GroupBy(item =>
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
                    coplanarOverlays.Contains(new PsxFaceInstanceKey(objectIndex, item.FaceIndex)));

            ModelDocumentGeometryAdapter.AddPrimitive(mesh, $"mat_{materialIndex:D3}", materialIndex, vertices,
                indices);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, nodeName, mesh, transform);
    }

    private static void AddPsxFace(
        List<ModelVertex> vertices,
        List<int> indices,
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        Vector4[]? gouraudPalette,
        (int Width, int Height) texDims,
        bool isCoplanarOverlay)
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

        if (face.IsSemiTransparent || isCoplanarOverlay)
        {
            // Geometric normal of the emitted CCW triangle (v0, v2, v1) —
            // outward per the winding convention below — so shadows/decals
            // lift away from the surface they overlay.
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
