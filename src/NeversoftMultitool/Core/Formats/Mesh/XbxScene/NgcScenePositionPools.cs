using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Source-order GameCube position arrays. Static display lists index the
///     scene-wide float pool; skinned display lists index the corresponding
///     object's signed-16 position list.
/// </summary>
public sealed class NgcScenePositionPools
{
    public required Vector3[] StaticPositions { get; init; }

    public required NgcSceneObjectPositionPool[] Objects { get; init; }
}

public sealed class NgcSceneObjectPositionPool
{
    public required int ObjectIndex { get; init; }

    public required uint RenderChecksum { get; init; }

    /// <summary>True when the source object serialized at least one render mesh.</summary>
    public required bool HasRenderChecksum { get; init; }

    /// <summary>
    ///     True when every mesh record in the source object carries the same
    ///     owner checksum. Collision binding rejects mixed-checksum objects.
    /// </summary>
    public required bool RenderChecksumIsUniform { get; init; }

    public required Vector3[] SkinPositions { get; init; }
}
