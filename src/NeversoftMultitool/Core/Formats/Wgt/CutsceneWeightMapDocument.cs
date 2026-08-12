namespace NeversoftMultitool.Core.Formats.Wgt;

/// <summary>The two compiled-platform suffixes that carry the source-proven WGT v1 layout.</summary>
public enum CutsceneWeightMapPlatform
{
    Ps2,
    Xbox
}

/// <summary>
///     Inspection-only representation of a compiled cutscene-head mesh-scaling weight map.
///     Applying it requires the selected skater profile's bone scales and an authoritative
///     companion-mesh vertex-order binding.
/// </summary>
public sealed class CutsceneWeightMapDocument
{
    public required CutsceneWeightMapPlatform Platform { get; init; }
    public required uint Version { get; init; }
    public required int SerializedSize { get; init; }
    public required string SerializedSha256 { get; init; }
    public required CutsceneWeightMapVertex[] Vertices { get; init; }
}

/// <summary>One raw three-weight/three-index tuple from the compiled WGT arrays.</summary>
public sealed record CutsceneWeightMapVertex(
    float Weight0,
    float Weight1,
    float Weight2,
    sbyte BoneIndex0,
    sbyte BoneIndex1,
    sbyte BoneIndex2)
{
    public float[] Weights => [Weight0, Weight1, Weight2];

    public sbyte[] BoneIndices => [BoneIndex0, BoneIndex1, BoneIndex2];
}
