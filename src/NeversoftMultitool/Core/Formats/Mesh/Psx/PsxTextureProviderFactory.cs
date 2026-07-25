using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Builds a neutral texture resolver that maps PSX texture hashes against
///     a primary <c>.psx</c> file and its sibling texture libraries. The
///     engine resolves texture identifiers through a global hash table fed by
///     EVERY loaded region, so a level's textures may live outside the
///     geometry file: retail pairs <c>*_g.psx</c> with <c>*_l.psx</c>; the
///     THPS1 prototype pairs suffixless level files (<c>skschl.psx</c>) with
///     <c>{stem}_l.psx</c>, variant files (<c>skschl_2.psx</c>) with the base
///     library, and additionally spools the shared <c>skatelib.psx</c> /
///     <c>sub_lib.psx</c> libraries. Shared by the static, animated, and GUI
///     character-preview paths.
/// </summary>
public static class PsxTextureProviderFactory
{
    /// <summary>
    ///     Ordered companion-library stems to consult (after the primary
    ///     file itself) when resolving a texture hash for <paramref name="stem" />.
    /// </summary>
    public static IReadOnlyList<string> GetCompanionLibraryStems(string stem)
    {
        var candidates = new List<string>();
        if (stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(stem[..^2] + "_l");
        }
        else if (!stem.EndsWith("_l", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(stem + "_l");
            var cut = stem.LastIndexOf('_');
            if (cut > 0)
            {
                // Two-player variants first try their own reduced library
                // (THPS1 final: skschl_2.psx -> skschll2.psx), then the full
                // base library.
                if (stem.EndsWith("_2", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(stem[..cut] + "l2");
                candidates.Add(stem[..cut] + "_l");
            }
        }

        // Apocalypse level regions use family libraries rather than the later
        // *_g / *_l convention. Most are a single shared file (city_1.psx,
        // city_2.psx, ... -> city_lib.psx), while the interior sequence adds
        // cumulative int2_lib/int3_lib files. Try the current and preceding
        // numbered libraries before the family base. The retail death region
        // uses the older spelling deathlib.psx, hence the final no-underscore
        // candidate. Keep these after the native pair candidates so later
        // games retain their own pairing when both conventions are present.
        var familySeparator = stem.IndexOf('_');
        if (familySeparator > 0)
        {
            var family = stem[..familySeparator];
            var suffix = stem.AsSpan(familySeparator + 1);
            var digitCount = 0;
            while (digitCount < suffix.Length && char.IsAsciiDigit(suffix[digitCount]))
                digitCount++;
            if (digitCount > 0 &&
                int.TryParse(suffix[..digitCount], out var regionNumber) &&
                regionNumber <= 32)
            {
                for (var region = regionNumber; region >= 2; region--)
                    candidates.Add($"{family}{region}_lib");
            }

            candidates.Add(family + "_lib");
            candidates.Add(family + "lib");
        }

        // THPS1-proto shared texture libraries, loaded alongside every level
        // region. Harmless elsewhere (candidates that don't exist are skipped).
        candidates.Add("skatelib");
        candidates.Add("sub_lib");
        return candidates;
    }

    public static MeshChecksumTextureResolver FromFile(string psxPath)
    {
        var stem = Path.GetFileNameWithoutExtension(psxPath);
        var dir = Path.GetDirectoryName(psxPath)!;
        var libraries = new List<string>();
        foreach (var candidate in GetCompanionLibraryStems(stem))
        {
            var matches = Directory.GetFiles(dir, candidate + ".psx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            if (matches.Length > 0 && !libraries.Contains(matches[0]))
                libraries.Add(matches[0]);
        }

        return hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(
                psxPath,
                hash,
                preserveRuntimeSemiTransparency: true);
            for (var i = 0; result == null && i < libraries.Count; i++)
            {
                result = PsxLibrary.ExtractTextureByHash(
                    libraries[i],
                    hash,
                    preserveRuntimeSemiTransparency: true);
            }

            if (result == null) return null;
            var (rgba, w, h) = result.Value;
            return ImageWriter.WritePngToMemory(w, h, rgba);
        };
    }
}
