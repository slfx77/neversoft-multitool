using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxMeshHeader
{
    public required ushort Version { get; init; }
    public PsxMeshFormatRevision FormatRevision { get; init; } = PsxMeshFormatRevision.Unknown;
    public required List<PsxMeshObject> Objects { get; init; }
    public required uint[] MeshTopPointers { get; init; }
    public required uint[] MeshNameHashes { get; init; }
    public required uint[] TextureHashes { get; init; }
    public Vector4[]? GouraudPalette { get; init; }
    public bool HasHierarchy { get; init; }
    public bool HasAnimChunk { get; init; }
    public float ScaleDivisor { get; init; }
    public float TranslationDivisor { get; init; }
}
