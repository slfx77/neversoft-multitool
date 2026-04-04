namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxMeshDumpSnapshot
{
    public required string FileName { get; init; }
    public required ushort Version { get; init; }
    public required bool HasHierarchy { get; init; }
    public required float ScaleDivisor { get; init; }
    public required float TranslationDivisor { get; init; }
    public required IReadOnlyList<PsxMeshDumpAttachmentSnapshot> Attachments { get; init; }
    public required IReadOnlyList<PsxMeshDumpObjectSnapshot> Objects { get; init; }
    public required IReadOnlyList<PsxMeshDumpMeshSnapshot> Meshes { get; init; }
}
