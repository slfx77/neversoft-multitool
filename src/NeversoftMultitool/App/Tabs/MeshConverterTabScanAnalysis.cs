using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool;

internal static class MeshConverterTabScanAnalysis
{
    // Cap on how many candidate files to header-probe. Recursive scans of a full
    // extracted game tree can produce thousands of candidates; probing every one
    // reads the file header, which dominates the pre-scan dialog latency. After
    // the cap, remaining candidates are silently ignored — the main parallel
    // scan still covers them.
    private const int MaxUnsupportedProbe = 200;

    public static List<ScanSummaryDialog.UnsupportedFile> FindUnsupportedFiles(IEnumerable<string> allFiles)
    {
        var unsupported = new List<ScanSummaryDialog.UnsupportedFile>();
        var probed = 0;
        foreach (var file in allFiles)
        {
            if (probed >= MaxUnsupportedProbe)
                break;

            var fileName = Path.GetFileName(file);
            if (!MeshTypeDetector.IsMeshCandidate(fileName))
                continue;

            var route = MeshTypeDetector.DetectByName(fileName);
            AddUnsupportedIfNeeded(unsupported, fileName, file, MeshTypeDetector.ReportsPartialSupport(route));
            probed++;
        }

        return unsupported;
    }

    public static int CountPotentiallySupportedFiles(IEnumerable<string> allFiles)
    {
        return allFiles.Count(static file => MeshTypeDetector.IsMeshCandidate(Path.GetFileName(file)));
    }

    private static void AddUnsupportedIfNeeded(
        List<ScanSummaryDialog.UnsupportedFile> unsupported,
        string fileName,
        string filePath,
        bool includePartial)
    {
        var probe = FormatProbe.ProbeMesh(filePath);
        var isUnsupported = probe.Support == FormatProbe.FormatSupport.Unsupported
                            || (includePartial && probe.Support == FormatProbe.FormatSupport.PartiallySupported);
        if (!isUnsupported)
            return;

        unsupported.Add(new ScanSummaryDialog.UnsupportedFile(
            fileName,
            probe.UnsupportedReason ?? "Unknown format"));
    }
}
