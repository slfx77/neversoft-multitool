namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelPrimitive
{
    public required string Name { get; init; }
    public int MaterialIndex { get; init; } = -1;
    public ModelPrimitiveTopology Topology { get; init; } = ModelPrimitiveTopology.Triangles;
    public required ModelVertex[] Vertices { get; init; }
    public required int[] Indices { get; init; }
    public ModelSkinBinding? Skin { get; init; }

    /// <summary>
    ///     Optional morph targets, in the order a <see cref="ModelMorphChannel" />'s
    ///     weights address them. Every primitive of a morphing mesh must carry the
    ///     same number of targets, since glTF weights apply mesh-wide.
    /// </summary>
    public IReadOnlyList<ModelMorphTarget>? MorphTargets { get; init; }

    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
    public int TriangleCount => Topology == ModelPrimitiveTopology.Triangles ? Indices.Length / 3 : 0;
}
