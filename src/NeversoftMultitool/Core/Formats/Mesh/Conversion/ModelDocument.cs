using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelDocument
{
    public required string Name { get; init; }
    public ModelSourceKind SourceKind { get; init; } = ModelSourceKind.Generic;
    public List<ModelScene> Scenes { get; } = [];
    public List<ModelNode> Nodes { get; } = [];
    public List<ModelMesh> Meshes { get; } = [];
    public List<RenderMaterial> Materials { get; } = [];
    public List<ModelTexture> Textures { get; } = [];
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
    public ModelNativeSource? NativeSource { get; init; }
    public int TriangleCount { get; set; }

    public static ModelDocument CreateNative(
        string name,
        ModelSourceKind sourceKind,
        ModelNativeSource nativeSource,
        int triangleCount = 0)
    {
        return new ModelDocument
        {
            Name = name,
            SourceKind = sourceKind,
            NativeSource = nativeSource,
            TriangleCount = triangleCount
        };
    }
}

public sealed class ModelScene
{
    public required string Name { get; init; }
    public List<int> RootNodeIndices { get; } = [];
}

public sealed class ModelNode
{
    public required string Name { get; init; }
    public int? MeshIndex { get; init; }
    public Matrix4x4 Transform { get; init; } = Matrix4x4.Identity;
    public List<int> ChildNodeIndices { get; } = [];
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
}

public sealed class ModelMesh
{
    public required string Name { get; init; }
    public List<ModelPrimitive> Primitives { get; } = [];
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
}

public sealed class ModelPrimitive
{
    public required string Name { get; init; }
    public int MaterialIndex { get; init; } = -1;
    public ModelPrimitiveTopology Topology { get; init; } = ModelPrimitiveTopology.Triangles;
    public required ModelVertex[] Vertices { get; init; }
    public required int[] Indices { get; init; }
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
    public int TriangleCount => Topology == ModelPrimitiveTopology.Triangles ? Indices.Length / 3 : 0;
}

public readonly record struct ModelVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Color,
    Vector2 TexCoord);

public sealed class RenderMaterial
{
    public required string Name { get; init; }
    public Vector4 BaseColor { get; set; } = Vector4.One;
    public int? TextureIndex { get; set; }
    public ModelAlphaMode AlphaMode { get; set; } = ModelAlphaMode.Opaque;
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool DoubleSided { get; set; } = true;
    public bool Unlit { get; set; } = true;
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
}

public sealed class ModelTexture
{
    public required string Name { get; init; }
    public byte[]? PngBytes { get; init; }
    public ModelTextureWrap WrapU { get; init; } = ModelTextureWrap.Repeat;
    public ModelTextureWrap WrapV { get; init; } = ModelTextureWrap.Repeat;
    public uint? NativeChecksum { get; init; }
}
