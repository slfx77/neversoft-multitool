namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

internal readonly record struct ArchiveRwDffCopyCandidate(
    string FileName,
    int NestingDepth,
    ArchiveRwDffFingerprint? Fingerprint);
