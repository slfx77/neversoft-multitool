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
///     Xbox/PC THUG2+THAW scenes: indexed triangles and degenerate-strip
///     decoding with multi-pass materials.
/// </summary>
internal static class XbxGeometryWriter
{
    public static void PopulateXbxScene(
        ModelDocument document,
        ParsedXbxScene scene,
        MeshChecksumTextureResolver? textureProvider,
        float coordinateScale = 1f)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Coordinate scale must be a finite positive number.");

        var materialMap = new Dictionary<uint, int>();
        for (var i = 0; i < scene.Materials.Length && i < document.Materials.Count; i++)
        {
            materialMap[scene.Materials[i].Checksum] = i;
            ApplyXbxMaterial(document, document.Materials[i], scene.Materials[i], textureProvider);
        }

        foreach (var sector in scene.Sectors)
        {
            foreach (var xbxMesh in sector.Meshes)
            {
                if (xbxMesh.Vertices.Length < 3 || xbxMesh.FaceIndices.Length < 3)
                    continue;

                if (!materialMap.TryGetValue(xbxMesh.MaterialChecksum, out var materialIndex))
                    materialIndex = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
                    {
                        Name = ModelDocumentGeometryAdapter.ResolveQbName(xbxMesh.MaterialChecksum, $"mat_{xbxMesh.MaterialChecksum:X8}")
                    });

                var mesh = new ModelMesh { Name = $"sector_{sector.Checksum:X8}" };
                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                if (xbxMesh.IsPreTriangulated)
                    AddXbxIndexedTriangles(vertices, indices, xbxMesh, coordinateScale);
                else
                    AddXbxTriangleStrip(vertices, indices, xbxMesh, coordinateScale);

                ModelDocumentGeometryAdapter.AddPrimitive(mesh, "triangles", materialIndex, vertices, indices);
                ModelDocumentGeometryAdapter.AddMeshNode(document, mesh.Name, mesh);
            }
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static void ApplyXbxMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        XbxMaterial material,
        MeshChecksumTextureResolver? textureProvider)
    {
        var textureAlphaMode = "OPAQUE";
        if (textureProvider != null && material.Passes.Length > 0)
        {
            var pass = material.Passes[0];
            if (pass.TextureChecksum != 0)
            {
                var pngBytes = textureProvider(pass.TextureChecksum);
                if (pngBytes != null)
                {
                    textureAlphaMode = Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes);
                    renderMaterial.TextureIndex ??= ModelDocumentGeometryAdapter.AddTexture(
                        document,
                        ModelDocumentGeometryAdapter.ResolveQbName(pass.TextureChecksum, $"tex_{pass.TextureChecksum:X8}"),
                        pngBytes,
                        pass.TextureChecksum,
                        pass.UAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat,
                        pass.VAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat);
                }
            }
        }

        var firstBlendMode = material.Passes.Length > 0 ? material.Passes[0].BlendMode : 0;
        if (textureAlphaMode == "BLEND" && (firstBlendMode != 0 || material.Sorted))
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        }
        else if (textureAlphaMode == "MASK" ||
                 (material.AlphaCutoff >= 1 && textureAlphaMode != "OPAQUE"))
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            renderMaterial.AlphaCutoff = material.AlphaCutoff >= 1
                ? material.AlphaCutoff / 255f
                : 0.5f;
        }
        else if (textureAlphaMode == "BLEND")
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        }
    }

    private static void AddXbxIndexedTriangles(
        List<ModelVertex> vertices,
        List<int> indices,
        XbxMesh mesh,
        float coordinateScale)
    {
        for (var i = 0; i + 2 < mesh.FaceIndices.Length; i += 3)
        {
            var i0 = mesh.FaceIndices[i];
            var i1 = mesh.FaceIndices[i + 1];
            var i2 = mesh.FaceIndices[i + 2];
            if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length)
                continue;
            ModelDocumentGeometryAdapter.AddTriangle(
                vertices,
                indices,
                MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
        }
    }

    private static void AddXbxTriangleStrip(
        List<ModelVertex> vertices,
        List<int> indices,
        XbxMesh mesh,
        float coordinateScale)
    {
        for (var i = 2; i < mesh.FaceIndices.Length; i++)
        {
            var i0 = mesh.FaceIndices[i - 2];
            var i1 = mesh.FaceIndices[i - 1];
            var i2 = mesh.FaceIndices[i];
            if (i0 == i1 || i1 == i2 || i0 == i2 ||
                i0 >= mesh.Vertices.Length ||
                i1 >= mesh.Vertices.Length ||
                i2 >= mesh.Vertices.Length)
            {
                continue;
            }

            if (i % 2 == 0)
            {
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices,
                    indices,
                    MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
            }
            else
            {
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices,
                    indices,
                    MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
            }
        }
    }

    private static ModelVertex MakeXbxVertex(XbxVertex vertex, float coordinateScale)
    {
        var color = vertex.HasColor
            ? new Vector4(
                Math.Min(vertex.Color.X, 1f),
                Math.Min(vertex.Color.Y, 1f),
                Math.Min(vertex.Color.Z, 1f),
                Math.Min(vertex.Color.W, 1f))
            : Vector4.One;

        return new ModelVertex(
            vertex.Position * coordinateScale,
            vertex.HasNormal ? ModelDocumentGeometryAdapter.NormalizeOrDefault(vertex.Normal) : Vector3.UnitY,
            color,
            vertex.TexCoord);
    }
}
