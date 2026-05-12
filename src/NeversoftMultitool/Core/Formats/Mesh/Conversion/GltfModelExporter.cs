using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

using GltfVertex = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

public sealed class GltfModelExporter : IModelExporter
{
    private const float Ps2SubtractiveAlphaScale = 0.30f;

    public MeshExportResult Export(ModelDocument document, MeshExportRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.OutputDirectory);

        return ExportGeneric(document, request);
    }

    public (byte[]? GlbBytes, int Triangles) BuildGlbBytes(ModelDocument document)
    {
        var (model, triangles) = BuildGenericModel(document);
        if (triangles == 0)
            return (null, 0);

        GltfNormalSmoother.SmoothNormals(model);
        using var ms = new MemoryStream();
        model.WriteGLB(ms);
        return (ms.ToArray(), triangles);
    }

    private static MeshExportResult ExportGeneric(ModelDocument document, MeshExportRequest request)
    {
        var (model, triangles) = BuildGenericModel(document);
        if (triangles == 0)
            return MeshExportResult.Empty;

        var outputPath = Path.Combine(request.OutputDirectory, (request.OutputStem ?? document.Name) + ".glb");
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return new MeshExportResult
        {
            OutputPaths = [outputPath],
            Triangles = triangles,
            MaterialCount = document.Materials.Count,
            TextureCount = document.Textures.Count
        };
    }

    private static (ModelRoot Model, int Triangles) BuildGenericModel(ModelDocument document)
    {
        var scene = new SceneBuilder();
        var materials = document.Materials.Select(material => BuildMaterial(material, document.Textures)).ToArray();
        var totalTriangles = 0;

        var roots = document.Scenes.Count > 0
            ? document.Scenes.SelectMany(static s => s.RootNodeIndices).ToArray()
            : Enumerable.Range(0, document.Nodes.Count).ToArray();
        var visited = new HashSet<int>();
        foreach (var rootIndex in roots)
            totalTriangles += AddNodeRecursive(scene, document, materials, rootIndex, Matrix4x4.Identity, visited);

        if (roots.Length == 0)
        {
            for (var i = 0; i < document.Nodes.Count; i++)
                totalTriangles += AddNodeRecursive(scene, document, materials, i, Matrix4x4.Identity, visited);
        }

        return (scene.ToGltf2(), totalTriangles);
    }

    private static int AddNodeRecursive(
        SceneBuilder scene,
        ModelDocument document,
        IReadOnlyList<MaterialBuilder> materials,
        int nodeIndex,
        Matrix4x4 parentTransform,
        HashSet<int> visited)
    {
        if ((uint)nodeIndex >= (uint)document.Nodes.Count || !visited.Add(nodeIndex))
            return 0;

        var node = document.Nodes[nodeIndex];
        var worldTransform = node.Transform * parentTransform;
        var totalTriangles = 0;

        if (node.MeshIndex.HasValue)
        {
            var meshIndex = node.MeshIndex!.Value;
            if ((uint)meshIndex >= (uint)document.Meshes.Count)
            {
                // Still traverse children if this node has a stale mesh reference.
            }
            else
            {
                var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(
                    document.Meshes[meshIndex].Name);
                foreach (var primitive in document.Meshes[meshIndex].Primitives)
                {
                    var material = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < materials.Count
                        ? materials[primitive.MaterialIndex]
                        : new MaterialBuilder("default").WithUnlitShader().WithDoubleSide(true);
                    var prim = mesh.UsePrimitive(material);
                    totalTriangles += AddTriangles(prim, primitive);
                }

                scene.AddRigidMesh(mesh, worldTransform);
            }
        }

        foreach (var childIndex in node.ChildNodeIndices)
            totalTriangles += AddNodeRecursive(scene, document, materials, childIndex, worldTransform, visited);

        return totalTriangles;
    }

    private static MaterialBuilder BuildMaterial(RenderMaterial material, IReadOnlyList<ModelTexture> textures)
    {
        var builder = new MaterialBuilder(material.Name)
            .WithBaseColor(material.BaseColor)
            .WithDoubleSide(material.DoubleSided);

        if (material.Unlit)
            builder.WithUnlitShader();

        if (material.TextureIndex is { } textureIndex &&
            (uint)textureIndex < (uint)textures.Count &&
            textures[textureIndex].PngBytes is { Length: > 0 } pngBytes)
        {
            var gltfPngBytes = ProcessTextureForPortableGltf(material, pngBytes);
            builder.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(gltfPngBytes));
            var channel = builder.GetChannel(KnownChannel.BaseColor);
            var wrapS = ToTextureWrapMode(textures[textureIndex].WrapU);
            var wrapT = ToTextureWrapMode(textures[textureIndex].WrapV);
            channel.Texture.WithSampler(wrapS, wrapT);
        }

        switch (material.AlphaMode)
        {
            case ModelAlphaMode.Mask:
                builder.WithAlpha(AlphaMode.MASK, material.AlphaCutoff);
                break;
            case ModelAlphaMode.Blend:
                builder.WithAlpha(AlphaMode.BLEND);
                break;
        }

        return builder;
    }

    private static byte[] ProcessTextureForPortableGltf(RenderMaterial material, byte[] pngBytes)
    {
        foreach (var metadata in material.NativeMetadata)
        {
            if (metadata is not Ps2GsRenderMetadata { Alpha: { } alpha })
                continue;

            var alphaBlend = (byte)(alpha & 0xFF);
            var aField = alphaBlend & 0x03;
            var bField = (alphaBlend >> 2) & 0x03;
            var cField = (alphaBlend >> 4) & 0x03;
            var dField = (alphaBlend >> 6) & 0x03;
            var fixScale = Math.Clamp((float)((alpha >> 32) & 0xFF) / 128f, 0f, 1f);
            var isAdditive = aField == 0 && bField == 2 && dField == 1 && cField is 0 or 2;
            var isSubtractive = aField == 2 && bField == 0 && dField == 1 && cField is 0 or 2;
            if (isAdditive)
            {
                var converted = MeshTextureHelper.ConvertAdditiveBlendTexture(pngBytes);
                return cField == 2
                    ? MeshTextureHelper.ScaleTextureAlpha(converted, fixScale)
                    : converted;
            }

            if (isSubtractive)
            {
                var converted = MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0);
                var scale = cField == 2 ? Ps2SubtractiveAlphaScale * fixScale : Ps2SubtractiveAlphaScale;
                return MeshTextureHelper.ScaleTextureAlpha(converted, scale);
            }
        }

        return pngBytes;
    }

    private static TextureWrapMode ToTextureWrapMode(ModelTextureWrap wrap) =>
        wrap == ModelTextureWrap.ClampToEdge
            ? TextureWrapMode.CLAMP_TO_EDGE
            : TextureWrapMode.REPEAT;

    private static int AddTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        ModelPrimitive primitive)
    {
        var triangles = 0;
        for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
        {
            var ia = primitive.Indices[i];
            var ib = primitive.Indices[i + 1];
            var ic = primitive.Indices[i + 2];
            if ((uint)ia >= (uint)primitive.Vertices.Length ||
                (uint)ib >= (uint)primitive.Vertices.Length ||
                (uint)ic >= (uint)primitive.Vertices.Length)
            {
                continue;
            }

            prim.AddTriangle(
                MakeVertex(primitive.Vertices[ia]),
                MakeVertex(primitive.Vertices[ib]),
                MakeVertex(primitive.Vertices[ic]));
            triangles++;
        }

        return triangles;
    }

    private static GltfVertex MakeVertex(ModelVertex vertex)
    {
        return new GltfVertex(
            new VertexPositionNormal(vertex.Position, vertex.Normal),
            new VertexColor1Texture1(vertex.Color, vertex.TexCoord));
    }
}
