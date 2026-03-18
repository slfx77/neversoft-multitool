using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool;

internal static class HashReviewerTextureLookup
{
    public static (byte[] Rgba, int Width, int Height)? TryExtractFromAllFiles(
        string buildsDir, HashReviewEntry entry, List<string> diagnostics)
    {
        var filesFound = 0;
        foreach (var psxPath in FindPsxFiles(buildsDir, entry.Files))
        {
            filesFound++;
            var result = PsxLibrary.ExtractTextureByHash(psxPath, entry.HashValue, diagnostics);
            if (result != null)
                return result;
        }

        if (filesFound == 0)
            diagnostics.Insert(0, $"No PSX files found for: {string.Join(", ", entry.Files)}");

        return null;
    }

    private static IEnumerable<string> FindPsxFiles(string buildsDir, string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            string[] matches;
            try
            {
                matches = Directory.GetFiles(buildsDir, fileName, SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var match in matches)
                yield return match;
        }
    }
}
