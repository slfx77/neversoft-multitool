namespace NeversoftMultitool.Core.Formats.Cas;

/// <summary>The two version-2 CAS polygon-removal record dialects proven by the THUG runtime.</summary>
public enum CasPolyRemovalPlatform
{
    Ps2,
    Xbox
}

/// <summary>
///     Inspection-only representation of a CAS polygon-removal sidecar. The records are preserved
///     as metadata; applying them to companion geometry requires platform-specific runtime bindings.
/// </summary>
public sealed class CasPolyRemovalDocument
{
    public required CasPolyRemovalPlatform Platform { get; init; }
    public required uint Version { get; init; }
    public required uint RemovalMask { get; init; }
    public required int SerializedSize { get; init; }
    public required string SerializedSha256 { get; init; }
    public required CasPolyRemovalEntry[] Entries { get; init; }
}

public abstract record CasPolyRemovalEntry(uint Mask);

/// <summary>
///     PS2 records identify a vertex-table reference. Resolving it to the DMA-chain ADC bit that
///     suppresses a polygon is deliberately outside the metadata parser.
/// </summary>
public sealed record CasPs2PolyRemovalEntry(uint Mask, int VertexReference)
    : CasPolyRemovalEntry(Mask);

/// <summary>
///     Xbox records pack the mesh load order and three triangle vertex indices into two raw words.
/// </summary>
public sealed record CasXboxPolyRemovalEntry(uint Mask, uint Data0, uint Data1)
    : CasPolyRemovalEntry(Mask)
{
    public uint MeshLoadOrder => Data0 >> 16;

    public ushort[] VertexIndices =>
    [
        (ushort)(Data0 & 0xFFFF),
        (ushort)(Data1 >> 16),
        (ushort)(Data1 & 0xFFFF)
    ];
}
