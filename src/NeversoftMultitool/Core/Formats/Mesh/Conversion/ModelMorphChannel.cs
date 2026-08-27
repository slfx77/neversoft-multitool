namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     A morph-weight track: how much of each of a mesh's morph targets is
///     applied over time. <see cref="Weights" /> is laid out key-major —
///     <c>Weights[key * TargetCount + target]</c> — mirroring how glTF stores a
///     weights sampler.
/// </summary>
public sealed class ModelMorphChannel
{
    /// <summary>Index into <see cref="ModelDocument.Meshes" /> of the mesh whose
    ///     targets these weights drive.</summary>
    public required int MeshIndex { get; init; }

    public required int TargetCount { get; init; }

    public required float[] Times { get; init; }

    public required float[] Weights { get; init; }

    public int KeyCount => Times.Length;
}
