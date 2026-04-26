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
}
