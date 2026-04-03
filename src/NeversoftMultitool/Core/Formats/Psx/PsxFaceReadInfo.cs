namespace NeversoftMultitool.Core.Formats.Psx;

internal sealed class PsxFaceReadInfo
{
    public required int RawFaceIndex { get; init; }
    public required long Offset { get; init; }
    public required ushort Flags { get; init; }
    public required ushort Length { get; init; }
    public required int BytesConsumed { get; init; }
    public required int UnderreadBytes { get; init; }
    public required int OverreadBytes { get; init; }
    public required bool IsLengthAligned { get; init; }
    public bool IsAccepted { get; set; }
    public int? AcceptedFaceIndex { get; set; }
    public string? RejectionReason { get; set; }
}
