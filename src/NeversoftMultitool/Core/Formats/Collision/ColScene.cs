namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>Parsed collision file containing one or more collision objects.</summary>
public sealed class ColScene
{
    public required int Version { get; init; }
    public required ColObject[] Objects { get; init; }

    public int TotalVertices => Objects.Sum(o => o.Vertices.Length);
    public int TotalTriangles => Objects.Sum(o => o.Faces.Length);
}
