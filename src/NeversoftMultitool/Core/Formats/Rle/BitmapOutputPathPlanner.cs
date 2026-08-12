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
        return Path.GetFileNameWithoutExtension(normalized);
    }
}
