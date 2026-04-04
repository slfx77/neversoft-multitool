namespace NeversoftMultitool;

internal sealed record MeshScanSummary(
    IReadOnlyList<ScanSummaryDialog.UnsupportedFile> UnsupportedFiles,
    int SupportedCount);
