using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeversoftMultitool;

internal static class FilePickerHelper
{
    /// <summary>
    ///     Opens a single-file picker with the given extension filters
    ///     (e.g. [".ps2", ".pak"]). Returns the chosen path or null if cancelled.
    /// </summary>
    internal static async Task<string?> PickFileAsync(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in extensions)
            picker.FileTypeFilter.Add(ext);
        if (picker.FileTypeFilter.Count == 0)
            picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>
    ///     Opens a save picker initialized for this unpackaged WinUI window.
    ///     The picker owns filename validation and overwrite confirmation; the
    ///     caller receives the selected filesystem path or null on cancellation.
    /// </summary>
    internal static async Task<string?> PickSaveFileAsync(
        string suggestedFileName,
        params (string Description, string[] Extensions)[] fileTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentNullException.ThrowIfNull(fileTypes);
        if (fileTypes.Length == 0)
            throw new ArgumentException("At least one file type is required.", nameof(fileTypes));

        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileName(suggestedFileName)
        };

        foreach (var (description, extensions) in fileTypes)
        {
            if (extensions.Length == 0)
                throw new ArgumentException("Each file type must have an extension.", nameof(fileTypes));

            picker.FileTypeChoices.Add(description, extensions);
        }

        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
