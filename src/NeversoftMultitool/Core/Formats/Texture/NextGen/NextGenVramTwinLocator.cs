namespace NeversoftMultitool.Core.Formats.Texture.NextGen;

/// <summary>
///     Finds the VRAM twin holding a PS3 texture dictionary's pixels.
/// </summary>
/// <remarks>
///     A PS3 <c>.tex.ps3</c> is metadata only; the surfaces live in a sibling
///     <c>.tvx.ps3</c>. Two copies of that name can exist — one beside the
///     dictionary inside an extracted <c>FOO.PAK</c>, and the real payload in the
///     sibling <c>FOO_VRAM.PAK</c> — so the choice is SIZE-VALIDATED against the
///     extent the dictionary's own records reference. Picking the neighbour
///     instead cost 49 of 49 pak-contained textures; with this rule those decode
///     pixel-exactly.
/// </remarks>
public static class NextGenVramTwinLocator
{
    /// <summary>
    ///     Loads the twin's bytes, or null when none is found (Xenon dictionaries
    ///     need no twin and always return null).
    /// </summary>
    public static byte[]? TryLoad(string dictionaryPath, byte[] dictionaryData)
    {
        var path = TryResolve(dictionaryPath, dictionaryData);
        if (path == null) return null;

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Resolves the twin's path without reading it.</summary>
    public static string? TryResolve(string dictionaryPath, byte[] dictionaryData)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dictionaryPath));
        if (directory == null) return null;

        var twinName = NextGenTexFile.GetVramTwinFileName(dictionaryPath);
        if (string.Equals(twinName, Path.GetFileName(dictionaryPath), StringComparison.OrdinalIgnoreCase))
            return null;

        var candidates = new List<string>();
        var parent = Path.GetDirectoryName(directory);
        if (parent != null)
        {
            candidates.Add(Path.Combine(
                parent, NextGenTexFile.GetVramTwinDirectoryName(directory), twinName));
        }

        candidates.Add(Path.Combine(directory, twinName));

        var required = NextGenTexFile.GetRequiredPayloadLength(dictionaryData);
        string? fallback = null;
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            if (new FileInfo(candidate).Length >= required) return candidate;
            fallback ??= candidate;
        }

        return fallback;
    }
}
