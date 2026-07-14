using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     PSX level/object mesh nodes: per-face colour/UV emission with the
///     semi-transparent decal lift and per-mesh node placement.
/// </summary>
internal static class PsxGeometryWriter
{
    public static void PopulatePsx(
        ModelDocument document,
        PsxMeshFile psxFile,
        MeshChecksumTextureResolver? textureProvider,
        PshFile? pshFile = null,
        bool flatSkeleton = false,
        IReadOnlySet<int>? flatBoneIndices = null)
    {
        if (PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(psxFile))
        {
            PsxSkinnedGeometryWriter.PopulatePsxSkinned(
                document, psxFile, pshFile, textureProvider,
                flatSkeleton, flatBoneIndices);
            ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
            return;
        }

        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache = new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>();
        var untexturedMaterial = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
        {
            Name = "untextured",
            BaseColor = new Vector4(0.7f, 0.7f, 0.7f, 1f),
            DoubleSided = false
        });

        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            var obj = psxFile.Objects[objectIndex];
            if (obj.MeshIndex >= psxFile.Meshes.Count)
                continue;

            var transform = Matrix4x4.CreateTranslation(
                PsxMeshSemantics.ToGltfPosition(PsxMeshSemantics.GetObjectOffset(psxFile, obj)));
            PopulatePsxMeshNode(
                document,
                psxFile,
                obj.MeshIndex,
                $"object_{objectIndex:D3}",
                transform,
                materialCache,
                textureDims,
                untexturedMaterial,
                textureProvider);
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static void PopulatePsxMeshNode(
        ModelDocument document,
        PsxMeshFile psxFile,
        int meshIndex,
        string nodeName,
        Matrix4x4 transform,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int> materialCache,
        Dictionary<uint, (int Width, int Height)> textureDims,
        int untexturedMaterial,
        MeshChecksumTextureResolver? textureProvider)
    {
        var psxMesh = psxFile.Meshes[meshIndex];
        if (psxMesh.Faces.Count == 0)
            return;

        var mesh = new ModelMesh { Name = PsxGeometryHelpers.ResolvePsxMeshName(psxFile, meshIndex) };
        foreach (var group in psxMesh.Faces.GroupBy(face =>
                     face.IsTextured && face.TextureHash != 0
                         ? (Hash: face.TextureHash, SemiTransparent: face.IsSemiTransparent,
                             DoubleSided: face.IsDoubleSided, BlendRate: face.BlendRate)
                         : (Hash: 0u, SemiTransparent: false, DoubleSided: face.IsDoubleSided,
                             BlendRate: 0)))
        {
            var materialIndex = group.Key.Hash == 0 && !group.Key.DoubleSided
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
            foreach (var face in group)
                AddPsxFace(vertices, indices, psxFile.Version, psxMesh, face, psxFile.GouraudPalette, texDims);

            ModelDocumentGeometryAdapter.AddPrimitive(mesh, $"mat_{materialIndex:D3}", materialIndex, vertices, indices);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, nodeName, mesh, transform);
    }

    /// <summary>
    ///     Lift applied to semi-transparent level faces along their outward
    ///     normal, in glTF units. The PS1 has no depth buffer — shadow meshes
    ///     and decals (road stripes etc.) sit exactly coplanar with the ground
    ///     and win by ordering-table draw order — but depth-tested glTF
    ///     viewers z-fight on coplanar geometry. 0.25 units is below the
    ///     minimum level-geometry grid step (1 raw unit / 2.25 ≈ 0.44), so
    ///     the lift is invisible while clearing depth-buffer precision.
    /// </summary>
    private const float PsxSemiTransparentFaceLift = 0.25f;

    private static void AddPsxFace(
        List<ModelVertex> vertices,
        List<int> indices,
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        Vector4[]? gouraudPalette,
        (int Width, int Height) texDims)
    {
        var (c0, c1, c2, c3) = PsxGeometryHelpers.ComputePsxFaceColors(version, face, gouraudPalette);
        var v0 = MakePsxVertex(version, mesh, face, 0, c0, texDims);
        var v1 = MakePsxVertex(version, mesh, face, 1, c1, texDims);
        var v2 = MakePsxVertex(version, mesh, face, 2, c2, texDims);
        var v3 = face.IsQuad ? MakePsxVertex(version, mesh, face, 3, c3, texDims) : default;

        if (face.IsSemiTransparent)
        {
            // Geometric normal of the emitted CCW triangle (v0, v2, v1) —
            // outward per the winding convention below — so shadows/decals
            // lift away from the surface they overlay.
            var geometricNormal = Vector3.Cross(
                v2.Position - v0.Position, v1.Position - v0.Position);
            var lengthSquared = geometricNormal.LengthSquared();
            if (lengthSquared > 1e-12f)
            {
                var lift = geometricNormal / MathF.Sqrt(lengthSquared) * PsxSemiTransparentFaceLift;
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
        (int Width, int Height) texDims)
    {
        var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
        if (vertexIndex >= mesh.Vertices.Count)
            return new ModelVertex(Vector3.Zero, Vector3.UnitY, color, Vector2.Zero);

        var nativeVertex = mesh.Vertices[(int)vertexIndex];
        var texCoord = face.GetTextureCoordinate(slot);
        return new ModelVertex(
            new Vector3(nativeVertex.X, -nativeVertex.Y, -nativeVertex.Z),
            PsxGeometryHelpers.ComputePsxVertexNormal(mesh, face, vertexIndex),
            color,
            PsxGeometryHelpers.ComputePsxTextureUv(version, face, texCoord.U, texCoord.V, texDims.Width, texDims.Height));
    }
}
