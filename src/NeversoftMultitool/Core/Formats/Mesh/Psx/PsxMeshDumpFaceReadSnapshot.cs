namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxMeshDumpFaceReadSnapshot
{
    public required int RawFaceIndex { get; init; }
    public required long Offset { get; init; }
    public required ushort Flags { get; init; }
    public required ushort Length { get; init; }
    public required int BytesConsumed { get; init; }
    public required int UnderreadBytes { get; init; }
    public required int OverreadBytes { get; init; }
    public required bool IsLengthAligned { get; init; }
    public required bool IsAccepted { get; init; }
    public required int? AcceptedFaceIndex { get; init; }
    public required string? RejectionReason { get; init; }
}
