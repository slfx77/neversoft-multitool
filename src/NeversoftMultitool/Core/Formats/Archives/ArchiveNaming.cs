namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Shared naming rules for archive extraction directories.
/// </summary>
public static class ArchiveNaming
{
    /// <summary>
    ///     Directory name an archive extracts into (created under the output directory).
    ///     Normally the file name minus its last extension, but localized PRE variants
    ///     (.prd German, .prf French, .prg German-on-NGC — THUG source gs_file.cpp:52-63)
    ///     keep the full file name: they ship beside a same-stem .pre/.prx and their
    ///     localized contents must not merge into the same directory.
    /// </summary>
    public static string GetExtractionStem(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (name.Length > 1 && name.Equals(ext, StringComparison.OrdinalIgnoreCase))
            return $"file{name}";

        return ext is ".prd" or ".prf" or ".prg" ? name : Path.GetFileNameWithoutExtension(name);
    }
}
