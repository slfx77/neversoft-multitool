using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     One morph target of a primitive: a per-vertex position delta from the base
///     mesh. Used by formats that animate by storing complete posed vertex sets
///     rather than by transforming a skeleton (the GBA skater), which glTF
///     expresses natively as morph targets.
/// </summary>
public sealed class ModelMorphTarget
{
    public required string Name { get; init; }

    /// <summary>Delta per vertex of the owning primitive, same length and order
    ///     as <see cref="ModelPrimitive.Vertices" />.</summary>
    public required Vector3[] PositionDeltas { get; init; }
}
