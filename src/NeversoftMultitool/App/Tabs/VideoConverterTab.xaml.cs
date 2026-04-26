using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool;

public sealed partial class VideoConverterTab : UserControl, IDisposable
{
    private static readonly string[] ArchiveExtensions = [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"];

    private readonly VideoConverterTabConversionController _conversionController = new();
    private readonly ObservableCollection<SfdFileEntry> _items = [];
    private readonly VideoConverterTabPreviewController _previewController;
    private bool _ffmpegAvailable;
    private string _inputDir = string.Empty;
    private string _outputDir = string.Empty;

    public VideoConverterTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        Unloaded += VideoConverterTab_Unloaded;
        _previewController = new VideoConverterTabPreviewController(new VideoPreviewView
        {
            PreviewPanel = PreviewPanel,
            PreviewSplitter = PreviewSplitter,
            SplitterColumn = SplitterColumn,
            PreviewColumn = PreviewColumn,
            PreviewLoading = PreviewLoading,
            VideoPlaceholderIcon = VideoPlaceholderIcon,
            PreviewFileNameText = PreviewFileNameText,
            PreviewInfoText = PreviewInfoText,
            PreviewErrorText = PreviewErrorText,
            PlayPauseButton = PlayPauseButton,
            StopButton = StopButton,
            PlaybackSlider = PlaybackSlider,
            CurrentTimeText = CurrentTimeText,
            TotalTimeText = TotalTimeText,
            VideoPlayer = VideoPlayer,
            PlayPauseIcon = PlayPauseIcon,
            TempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "VideoPreview")
        });
        CheckFfmpeg();
    }

    public void Dispose()
    {
        Unloaded -= VideoConverterTab_Unloaded;
        _previewController.Dispose();
        _conversionController.Dispose();
    }

    private void CheckFfmpeg()
    {
        _ffmpegAvailable = SfdConverter.FindFfmpeg() != null;
        FfmpegWarning.IsOpen = !_ffmpegAvailable;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null)
            return;

        _inputDir = path;
        InputPathText.Text = _inputDir;
        _previewController.ClearPreview();

        _items.Clear();
        foreach (var filePath in VideoConverterTabOperations.FindVideoFiles(_inputDir))
            _items.Add(VideoConverterTabOperations.CreateEntry(filePath));

        UpdateUiState();
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;
        _previewController.ClearPreview();
        _items.Clear();

        await Task.Run(() =>
        {
            var backend = ArchiveAssetBackend.TryOpen(path);
            if (backend == null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance?.SetStatus($"{Path.GetFileName(path)}: unsupported archive.");
                    UpdateUiState();
                });
                return;
            }

            var archiveName = Path.GetFileName(path);
            var entries = new List<SfdFileEntry>();
            foreach (var archiveEntry in backend.Entries)
            {
                if (!VideoConverterTabOperations.IsVideoFile(archiveEntry.Name)) continue;
                entries.Add(VideoConverterTabOperations.CreateEntryForArchiveEntry(backend, archiveEntry));
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var entry in entries.OrderBy(en => en.FileName, StringComparer.OrdinalIgnoreCase))
                    _items.Add(entry);

                MainWindow.Instance?.SetStatus(entries.Count == 0
                    ? $"{archiveName}: no video entries."
                    : $"Found {entries.Count} video entrie(s) in {archiveName}.");

                UpdateUiState();
            });
        });
    }

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null)
            return;

        _outputDir = path;
        OutputPathText.Text = _outputDir;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _items.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasFiles && hasOutput && _ffmpegAvailable;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        await _conversionController.ConvertAsync(
            _items,
            _outputDir,
            DispatcherQueue,
            ConvertButton,
            CancelButton,
            ConversionProgress);
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        await _conversionController.CancelAsync(ConvertButton, CancelButton);
    }

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilesListView.SelectedItem is SfdFileEntry entry)
        {
            await _previewController.ShowPreviewAsync(entry, _ffmpegAvailable);
            return;
        }

        _previewController.ClearPreview();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        _previewController.TogglePlayPause();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _previewController.Stop();
    }

    private void PlaybackSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _previewController.Seek(e.NewValue);
    }

    private void VideoConverterTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
