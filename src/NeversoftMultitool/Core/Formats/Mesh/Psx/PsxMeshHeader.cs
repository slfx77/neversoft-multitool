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

    /// <summary>
    ///     True when the file's chunk stream contains the 0x2A/0x2C animation
    ///     chunk that sets the runtime's IsSuper flag. A HIER chunk alone only
    ///     supplies parent indices and does not make the file a super.
    /// </summary>
    public bool IsSuperModel { get; init; }

    /// <summary>
    ///     True when mesh headers carry the 4-byte zMax/NextLOD field after
    ///     the bounding box. An exporter-revision trait, uniform across the
    ///     whole file: always present for v4/v6, present for THPS1-proto v3
    ///     (NeversoftV3), absent for Apocalypse v3. Decided once per file by
    ///     majority vote — per-mesh probing misfires on individual level
    ///     meshes and shears their vertex parse by 4 bytes.
    /// </summary>
    public bool HasMeshLodField { get; init; }

    public float ScaleDivisor { get; init; }
    public float TranslationDivisor { get; init; }
}
