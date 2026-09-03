using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Core.Formats.Rle;

internal static class BitmapOutputPathPlanner
{
    internal readonly record struct PlannedOutput(string Source, string RelativePngPath);

    public static IReadOnlyList<PlannedOutput> Plan(
        IReadOnlyList<string> sourceDisplayNames,
        string? inputRoot)
    {
        // A PNG is already the converter's output format. Reserve each source
        // PNG's natural stem so neither a primary output nor a TIFF's derived
        // foo_mipN.png output can replace it when output points at the input
        // tree. Repeat the suffix until both the primary and every possible
        // derived mip name are clear; this also covers authored names such as
        // foo_converted_mip1.png.
        var sourcePngStems = sourceDisplayNames
            .Where(IsPng)
            .Select(GetStem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string PreferredStem(string source)
        {
            var stem = GetStem(source);
            if (IsPng(source))
                return stem;

            while (sourcePngStems.Contains(stem)
                   || BitmapFile.IsTiffExtension(source)
                   && sourcePngStems.Any(pngStem => IsMipOutputStem(stem, pngStem)))
            {
                stem += "_converted";
            }

            return stem;
        }

        // MeshOutputPathPlanner can reserve a file's complete output-name set.
        // A TIFF's exact level count is not known until the caller reads it, but
        // only aliases that collide with another input's preferred primary name
        // affect planning. Reserve those conservatively; unrelated mip names do
        // not need to be enumerated.
        var preferredStems = sourceDisplayNames
            .Select(PreferredStem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plans = MeshOutputPathPlanner.Plan(
            sourceDisplayNames,
            PreferredStem,
            (source, proposedStem) =>
            {
                var outputs = new List<string> { proposedStem };
                if (BitmapFile.IsTiffExtension(source))
                {
                    outputs.AddRange(preferredStems.Where(
                        candidate => IsMipOutputStem(proposedStem, candidate)));
                }

                return outputs;
            },
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

    internal static bool IsInPlaceFileSystemOutput(AssetSource source, string outputPath)
    {
        if (source.FileSystemPath is not { Length: > 0 } sourcePath)
            return false;

        try
        {
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return Path.GetFullPath(sourcePath).Equals(
                Path.GetFullPath(outputPath),
                comparison);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsPng(string source)
    {
        var normalized = source.Replace("::", "/").Replace('\\', '/');
        return Path.GetFileName(normalized).EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMipOutputStem(string baseStem, string candidateStem)
    {
        var prefix = baseStem + "_mip";
        if (!candidateStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = candidateStem.AsSpan(prefix.Length);
        if (suffix.IsEmpty || suffix[0] == '0')
            return false;

        foreach (var character in suffix)
            if (character is < '0' or > '9')
                return false;

        return true;
    }
}
