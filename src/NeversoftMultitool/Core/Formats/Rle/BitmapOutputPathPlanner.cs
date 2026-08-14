using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Core.Formats.Rle;

internal static class BitmapOutputPathPlanner
{
    internal readonly record struct PlannedOutput(string Source, string RelativePngPath);

    public static IReadOnlyList<PlannedOutput> Plan(
        IReadOnlyList<string> sourceDisplayNames,
        string? inputRoot)
    {
        var plans = MeshOutputPathPlanner.Plan(
            sourceDisplayNames,
            GetStem,
            inputRoot);

        return
        [
            .. plans.Select(static plan => new PlannedOutput(
                plan.File,
                Path.Combine(plan.Subdirectory, plan.Stem + ".png")))
        ];
    }

    private static string GetStem(string source)
    {
        var normalized = source.Replace("::", "/").Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length > 0)
            return stem;

        // Supported extension-only leaves such as ".rle" have no ordinary
        // file-name stem. Preserve their spelling while supplying the output
        // planner with a safe, non-empty name.
        var extension = Path.GetExtension(fileName);
        return extension.Length > 1 ? $"_{extension[1..]}" : stem;
    }
}
