#if WINDOWS_GUI
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;

namespace NeversoftMultitool;

/// <summary>
///     Shows a pre-scan summary dialog when unsupported files are detected.
///     Returns true if the user wants to continue with supported files only.
/// </summary>
public static class ScanSummaryDialog
{
    public record UnsupportedFile(string FileName, string Reason);

    /// <summary>
    ///     Shows the dialog if there are unsupported files. Returns true to continue, false to cancel.
    ///     If no unsupported files, returns true immediately (no dialog shown).
    /// </summary>
    public static async Task<bool> ShowIfNeeded(
        XamlRoot xamlRoot,
        int supportedCount,
        IReadOnlyList<UnsupportedFile> unsupportedFiles)
    {
        if (unsupportedFiles.Count == 0)
            return true;

        var listItems = new StackPanel { Spacing = 4 };
        foreach (var file in unsupportedFiles)
        {
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            item.Children.Add(new TextBlock
            {
                Text = file.FileName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 250
            });
            item.Children.Add(new TextBlock
            {
                Text = file.Reason,
                Foreground =
 (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            listItems.Children.Add(item);
        }

        var scrollViewer = new ScrollViewer
        {
            Content = listItems,
            MaxHeight = 200,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"Ready to process: {supportedCount} files"
        });

        var warningHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        warningHeader.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA",
            FontSize = 16,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
        });
        warningHeader.Children.Add(new TextBlock
        {
            Text = $"Unsupported files ({unsupportedFiles.Count}):",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(warningHeader);
        content.Children.Add(scrollViewer);

        var dialog = new ContentDialog
        {
            Title = "Scan Summary",
            Content = content,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
#endif
