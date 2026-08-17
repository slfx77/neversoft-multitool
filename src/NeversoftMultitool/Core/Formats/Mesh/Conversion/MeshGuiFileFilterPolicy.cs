namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pure decisions behind the mesh file table's filename filter. The filter
///     narrows only the DISPLAYED rows: batch conversion and rendering keep
///     operating on every checked entry, so the convert-button label surfaces
///     how many checked entries the active filter is hiding.
/// </summary>
internal static class MeshGuiFileFilterPolicy
{
    /// <summary>
    ///     Case-insensitive substring match over the row's displayed name.
    ///     Falls back to the bare file name when the scanner did not populate
    ///     a relative path (mirrors the display fallback on the entry model).
    /// </summary>
    public static bool Matches(string? relativePath, string? fileName, string? filterText)
    {
        if (string.IsNullOrEmpty(filterText)) return true;
        var haystack = string.IsNullOrEmpty(relativePath) ? fileName : relativePath;
        return haystack?.Contains(filterText, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    ///     Convert-button label. The count is ALL checked entries (hidden ones
    ///     still convert); a nonzero hidden count is called out so a filtered
    ///     view can never silently misrepresent what the button will do.
    /// </summary>
    public static string ConvertButtonLabel(int checkedCount, int hiddenCheckedCount)
    {
        var label = checkedCount switch
        {
            0 => "Convert files",
            1 => "Convert 1 file",
            _ => $"Convert {checkedCount} files"
        };
        return hiddenCheckedCount > 0 ? $"{label} ({hiddenCheckedCount} hidden)" : label;
    }
}
