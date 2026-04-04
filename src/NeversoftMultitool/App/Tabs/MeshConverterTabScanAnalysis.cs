using NeversoftMultitool.Core;

namespace NeversoftMultitool;

internal static class MeshConverterTabScanAnalysis
{
    private static readonly string[] MeshFormatsWithPartialWarnings =
    [
        ".skin.xbx", ".mdl.xbx", ".scn.xbx",
        ".skin.wpc", ".mdl.wpc", ".scn.wpc",
        ".col.xbx", ".col.wpc", ".col.ps2", ".col.psp",
        ".bsp"
    ];

    private static readonly string[] Ps2SceneSuffixes = [".skin.ps2", ".mdl.ps2", ".iskin.ps2"];

    private static readonly string[] SupportedMeshSuffixes =
    [
        ".bsp",
        ".col.xbx", ".col.wpc", ".col.ps2",
        ".skin.ps2", ".mdl.ps2", ".iskin.ps2", ".geom.ps2",
        ".skin.xbx", ".mdl.xbx",
        ".skin.wpc", ".mdl.wpc"
    ];

    private static readonly string[] PlatformSuffixes = [".ps2", ".xbx", ".wpc"];

    public static List<ScanSummaryDialog.UnsupportedFile> FindUnsupportedFiles(IEnumerable<string> allFiles)
    {
        var unsupported = new List<ScanSummaryDialog.UnsupportedFile>();
        foreach (var file in allFiles)
        {
            var fileName = Path.GetFileName(file);
            if (OrdinalFileName.HasAnySuffix(fileName, MeshFormatsWithPartialWarnings))
            {
                AddUnsupportedIfNeeded(unsupported, fileName, file, true);
            }
            else if (OrdinalFileName.HasAnySuffix(fileName, Ps2SceneSuffixes)
                     || OrdinalFileName.HasExtension(file, ".skin")
                     || OrdinalFileName.HasExtension(file, ".mdl"))
            {
                AddUnsupportedIfNeeded(unsupported, fileName, file, false);
            }
        }

        return unsupported;
    }

    public static int CountPotentiallySupportedFiles(IEnumerable<string> allFiles)
    {
        return allFiles.Count(static file =>
        {
            var fileName = Path.GetFileName(file);
            return OrdinalFileName.HasExtension(file, ".ddm")
                   || OrdinalFileName.HasExtension(file, ".psx")
                   || OrdinalFileName.HasExtension(file, ".skn")
                   || OrdinalFileName.HasExtension(file, ".col")
                   || OrdinalFileName.HasAnySuffix(fileName, SupportedMeshSuffixes)
                   || ((OrdinalFileName.HasExtension(file, ".skin")
                        || OrdinalFileName.HasExtension(file, ".mdl"))
                       && !OrdinalFileName.HasAnySuffix(fileName, PlatformSuffixes));
        });
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
