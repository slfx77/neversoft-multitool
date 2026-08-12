using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool;

internal static class HashReviewerTextureLookup
{
    public static (byte[] Rgba, int Width, int Height)? TryExtractFromAllFiles(
        string buildsDir, HashReviewEntry entry, List<string> diagnostics)
    {
        return PsxTextureReviewLookup.TryExtractFromAllFiles(
            buildsDir,
            entry.Files,
            entry.HashValue,
            diagnostics);
    }
}
