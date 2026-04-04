using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool;

public sealed partial class VideoConverterTab : UserControl, IDisposable
{
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
