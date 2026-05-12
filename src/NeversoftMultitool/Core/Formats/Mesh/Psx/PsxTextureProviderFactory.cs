using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Builds a neutral texture resolver that maps PSX texture hashes against
///     a primary <c>.psx</c> file and (for level
///     geometry files named <c>*_g.psx</c>) its sibling library <c>*_l.psx</c>.
///     Shared by the static, animated, and GUI character-preview paths.
/// </summary>
public static class PsxTextureProviderFactory
{
    public static MeshChecksumTextureResolver FromFile(string psxPath)
    {
        var stem = Path.GetFileNameWithoutExtension(psxPath);
        string? companionLibPath = null;
        if (stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
        {
            var libStem = stem[..^2] + "_l";
            var dir = Path.GetDirectoryName(psxPath)!;
            var candidates = Directory.GetFiles(dir, libStem + ".psx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            if (candidates.Length > 0)
                companionLibPath = candidates[0];
        }

        return hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(psxPath, hash);
            if (result == null && companionLibPath != null)
                result = PsxLibrary.ExtractTextureByHash(companionLibPath, hash);
            if (result == null) return null;
            var (rgba, w, h) = result.Value;
            return ImageWriter.WritePngToMemory(w, h, rgba);
        };
    }
}
