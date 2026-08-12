namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Shared file-picker and compound-extension routing contract for the GUI
///     archive extractor.
/// </summary>
public static class ArchiveExtractorRoute
{
    private static readonly string[] PickerExtensionExtras =
    [
        ".ps2", ".ngc", ".wpc", ".xbx", ".bin"
    ];

    /// <summary>
    ///     Extensions visible in the archive picker. The detector's canonical
    ///     archive extensions are supplemented by platform suffixes used by
    ///     compound archive names and by the raw-disc <c>.bin</c> entry point.
    /// </summary>
    public static IReadOnlyList<string> PickerExtensions { get; } = Array.AsReadOnly(
        ArchiveTypeDetector.ArchiveExtensions
            .Concat(PickerExtensionExtras)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    /// <summary>
    ///     Resolves a canonical archive extension while retaining unknown final
    ///     extensions so standalone platform-suffixed inputs can still be
    ///     content-gated by the extractor.
    /// </summary>
    public static string ResolveExtension(string filePath)
    {
        return ArchiveTypeDetector.GetArchiveExtension(filePath);
    }
}
