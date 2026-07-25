using System.Collections.ObjectModel;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private bool _outputManuallySet;
    private CancellationTokenSource? _probeCts;
    private bool _videoDragging;
    private Point _videoDragStart;
    private double _videoDragStartH;
    private double _videoDragStartV;

    public VideoConverterTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        Unloaded += VideoConverterTab_Unloaded;
        _previewController = new VideoConverterTabPreviewController(new VideoPreviewView
        {
            PreviewLoading = PreviewLoading,
            VideoPlaceholderPanel = VideoPlaceholderPanel,
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
        // 100% zoom needs the opened stream's natural size, so re-apply
        // whenever a new source finishes opening.
        _previewController.MediaOpened = ApplyVideoZoom;
        RoundedClipHelper.Apply(VideoPlayer, 8);
        CheckFfmpeg();
    }

    // ─── Zoom (fit / 100%) ────────────────────────────────────────────────

    private bool IsVideoActualSize => VideoZoomCombo.SelectedIndex == 1;

    public void Dispose()
    {
        Unloaded -= VideoConverterTab_Unloaded;
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
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
        DefaultOutputToInput(path);
        _previewController.ClearPreview();
        _items.Clear();

        // The recursive scan sniffs headers per file — keep it off the UI thread.
        var inputDir = _inputDir;
        var entries = await Task.Run(() =>
            VideoConverterTabOperations.FindVideoFiles(inputDir)
                .Select(filePath => VideoConverterTabOperations.CreateEntry(filePath, inputDir))
                .ToList());

        foreach (var entry in entries)
            _items.Add(entry);

        UpdateUiState();
        StartBackgroundProbe();
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;
        if (_inputDir.Length > 0)
            DefaultOutputToInput(_inputDir);
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
                foreach (var entry in entries.OrderBy(en => en.RelativePath, StringComparer.OrdinalIgnoreCase))
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
        _outputManuallySet = true;
        OutputPathText.Text = _outputDir;
        UpdateUiState();
    }

    private void DefaultOutputToInput(string dir)
    {
        if (_outputManuallySet)
            return;

        _outputDir = dir;
        OutputPathText.Text = dir;
    }

    /// <summary>
    ///     Fills Duration/Resolution after the list is shown: ffprobe spawns a
    ///     process per file, far too slow to run inline on a recursive scan.
    /// </summary>
    private void StartBackgroundProbe()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        var token = cts.Token;
        var entries = _items.ToList();

        _ = Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                if (token.IsCancellationRequested)
                    return;
                if (entry.Source.FileSystemPath is not { } filePath)
                    continue;

                var (duration, resolution) = VideoConverterTabOperations.ProbeFile(filePath);
                DispatcherQueue.TryEnqueue(() =>
                {
                    entry.DurationDisplay = duration;
                    entry.ResolutionDisplay = resolution;
                });
            }
        }, token);
    }

    private void UpdateUiState()
    {
        var hasFiles = _items.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasFiles && hasOutput && _ffmpegAvailable;
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = CheckAllBox.IsChecked == true;
        foreach (var entry in _items)
            entry.IsChecked = isChecked;
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

    private void VideoZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyVideoZoom();
    }

    private void ApplyVideoZoom()
    {
        // SelectedIndex="0" raises SelectionChanged during InitializeComponent,
        // before the later-declared elements (and the controller) exist.
        if (VideoScroller is null || VideoPlayer is null || _previewController is null)
            return;

        if (IsVideoActualSize && _previewController.NaturalVideoSize is { } size)
        {
            // 1:1 pixels — size the player to the stream, scrollbars when larger.
            VideoPlayer.Stretch = Stretch.None;
            VideoPlayer.Width = size.Width;
            VideoPlayer.Height = size.Height;
            VideoScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VideoScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            VideoScroller.HorizontalScrollMode = ScrollMode.Enabled;
            VideoScroller.VerticalScrollMode = ScrollMode.Enabled;
        }
        else
        {
            VideoPlayer.Stretch = Stretch.Uniform;
            VideoPlayer.ClearValue(WidthProperty);
            VideoPlayer.ClearValue(HeightProperty);
            VideoScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VideoScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VideoScroller.HorizontalScrollMode = ScrollMode.Disabled;
            VideoScroller.VerticalScrollMode = ScrollMode.Disabled;
            VideoScroller.ChangeView(0, 0, null, true);
        }
    }

    // ─── Drag-to-pan (100% mode) ──────────────────────────────────────────

    private void VideoHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsVideoActualSize) return;

        _videoDragging = true;
        _videoDragStart = e.GetCurrentPoint(VideoScroller).Position;
        _videoDragStartH = VideoScroller.HorizontalOffset;
        _videoDragStartV = VideoScroller.VerticalOffset;
        VideoHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void VideoHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_videoDragging) return;

        var pos = e.GetCurrentPoint(VideoScroller).Position;
        VideoScroller.ChangeView(
            _videoDragStartH - (pos.X - _videoDragStart.X),
            _videoDragStartV - (pos.Y - _videoDragStart.Y),
            null,
            true);
        e.Handled = true;
    }

    private void VideoHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_videoDragging) return;
        _videoDragging = false;
        VideoHost.ReleasePointerCaptures();
    }

    private void VideoConverterTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
