using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool;

/// <summary>Single-selection browser for source-rig entries already catalogued in place.</summary>
internal static class ArchiveSourceRigDialog
{
    public static async Task<ArchiveSourceRigCandidate?> ShowAsync(
        XamlRoot xamlRoot,
        IReadOnlyList<ArchiveSourceRigCandidate> candidates,
        CancellationToken cancellationToken,
        string title = "Choose animation source rig")
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        cancellationToken.ThrowIfCancellationRequested();

        var list = new ListView
        {
            ItemsSource = candidates,
            DisplayMemberPath = nameof(ArchiveSourceRigCandidate.DisplayName),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 420,
            MinWidth = 520
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = list,
            PrimaryButtonText = "Use selected rig",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot
        };
        list.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is ArchiveSourceRigCandidate;

        var dispatcher = dialog.DispatcherQueue;
        using var registration = cancellationToken.Register(() =>
            dispatcher.TryEnqueue(dialog.Hide));

        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary
                && !cancellationToken.IsCancellationRequested
                ? list.SelectedItem as ArchiveSourceRigCandidate
                : null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
