namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     Vertex with position, normal, vertex color, and texture coordinates.
/// </summary>
public readonly record struct DdmVertex(
    float X,
    float Y,
    float Z,
    float NX,
    float NY,
    float NZ,
    byte R,
    byte G,
    byte B,
    byte A,
    float U,
    float V);
