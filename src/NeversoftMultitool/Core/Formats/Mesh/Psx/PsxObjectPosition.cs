namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     World-space position for a DDM mesh object, extracted from a PSX file's Object Position section.
/// </summary>
public readonly record struct PsxObjectPosition(float X, float Y, float Z, ushort MeshIndex);
