using NeversoftMultitool.Core;

namespace NeversoftMultitool;

internal static class MeshConverterTabScanAnalysis
{
    public static List<ScanSummaryDialog.UnsupportedFile> FindUnsupportedFiles(IEnumerable<string> allFiles)
    {
        var unsupported = new List<ScanSummaryDialog.UnsupportedFile>();
        foreach (var file in allFiles)
        {
            var lower = Path.GetFileName(file).ToLowerInvariant();
            if (lower.EndsWith(".skin.xbx") || lower.EndsWith(".mdl.xbx") || lower.EndsWith(".scn.xbx")
                || lower.EndsWith(".skin.wpc") || lower.EndsWith(".mdl.wpc") || lower.EndsWith(".scn.wpc")
                || lower.EndsWith(".col.xbx") || lower.EndsWith(".col.wpc")
                || lower.EndsWith(".col.ps2") || lower.EndsWith(".col.psp")
                || lower.EndsWith(".bsp"))
            {
                var probe = FormatProbe.ProbeMesh(file);
                if (probe.Support != FormatProbe.FormatSupport.Supported)
                    unsupported.Add(new ScanSummaryDialog.UnsupportedFile(
                        Path.GetFileName(file)!,
                        probe.UnsupportedReason ?? "Unknown format"));
            }
            else if (lower.EndsWith(".skin.ps2") || lower.EndsWith(".mdl.ps2") || lower.EndsWith(".iskin.ps2"))
            {
                var probe = FormatProbe.ProbeMesh(file);
                if (probe.Support == FormatProbe.FormatSupport.Unsupported)
                    unsupported.Add(new ScanSummaryDialog.UnsupportedFile(
                        Path.GetFileName(file)!,
                        probe.UnsupportedReason ?? "Unknown format"));
            }
            else if (Path.GetExtension(lower) is ".skin" or ".mdl")
            {
                var probe = FormatProbe.ProbeMesh(file);
                if (probe.Support == FormatProbe.FormatSupport.Unsupported)
                    unsupported.Add(new ScanSummaryDialog.UnsupportedFile(
                        Path.GetFileName(file)!,
                        probe.UnsupportedReason ?? "Unknown format"));
            }
        }

        return unsupported;
    }

    public static int CountPotentiallySupportedFiles(IEnumerable<string> allFiles)
    {
        return allFiles.Count(static file =>
        {
            var lower = Path.GetFileName(file).ToLowerInvariant();
            var extension = Path.GetExtension(file).ToLowerInvariant();
            return extension is ".ddm" or ".psx" or ".skn" or ".bsp" ||
                   lower.EndsWith(".col.xbx") || lower.EndsWith(".col.wpc") ||
                   lower.EndsWith(".col.ps2") || extension == ".col" ||
                   lower.EndsWith(".skin.ps2") || lower.EndsWith(".mdl.ps2") ||
                   lower.EndsWith(".iskin.ps2") || lower.EndsWith(".geom.ps2") ||
                   lower.EndsWith(".skin.xbx") || lower.EndsWith(".mdl.xbx") ||
                   lower.EndsWith(".skin.wpc") || lower.EndsWith(".mdl.wpc") ||
                   (extension is ".skin" or ".mdl" && !lower.EndsWith(".ps2") &&
                    !lower.EndsWith(".xbx") && !lower.EndsWith(".wpc"));
        });
    }
}
