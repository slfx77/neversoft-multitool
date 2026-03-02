using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool;

public sealed partial class SfdConverterTab : UserControl
{
    private readonly ObservableCollection<SfdFileEntry> _items = [];
    private readonly Dictionary<string, string> _previewCache = [];

    // Temp file cache for preview
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "VideoPreview");
    private CancellationTokenSource? _cts;
    private bool _ffmpegAvailable;
    private string _inputDir = "";

    // Playback
    private MediaPlayer? _mediaPlayer;
    private string _outputDir = "";
    private DispatcherTimer? _positionTimer;
    private CancellationTokenSource? _previewCts;
    private bool _updatingSlider;

    public SfdConverterTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        CheckFfmpeg();
    }

    private void CheckFfmpeg()
    {
        _ffmpegAvailable = SfdConverter.FindFfmpeg() != null;
        FfmpegWarning.IsOpen = !_ffmpegAvailable;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;

        ClearPreview();
        _previewCache.Clear();

        _items.Clear();
        var videoFiles = Directory.GetFiles(_inputDir, "*.sfd", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(_inputDir, "*.SFD", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(_inputDir, "*.str", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(_inputDir, "*.STR", SearchOption.TopDirectoryOnly))
                .Where(IsStrVideoFile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in videoFiles)
        {
            var fileName = Path.GetFileName(filePath);
            var fileSize = new FileInfo(filePath).Length;

            var (duration, resolution) = ProbeFile(filePath);
            _items.Add(new SfdFileEntry
            {
                FileName = fileName,
                FilePath = filePath,
                DurationDisplay = duration,
                ResolutionDisplay = resolution,
                SizeDisplay = FormatFileSize(fileSize)
            });
        }

        UpdateUiState();
    }

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

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
        if (_items.Count == 0 || string.IsNullOrEmpty(_outputDir) || !_ffmpegAvailable) return;

        _cts = new CancellationTokenSource();

        foreach (var file in _items)
            file.Status = ExtractionStatus.Pending;

        ConvertButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ConversionProgress.Visibility = Visibility.Visible;
        ConversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalConverted = 0;
        var totalFiles = _items.Count;
        var token = _cts.Token;
        var dispatcher = DispatcherQueue;
        var outputDir = _outputDir;

        var entries = _items.ToList();

        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested) break;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var result = ConvertFile(entry.FilePath, outputDir,
                        new Progress<double>(p =>
                            dispatcher.TryEnqueue(() => entry.ConvertProgress = p * 100)),
                        token);

                    var processed = Interlocked.Increment(ref filesProcessed);

                    if (result.Success)
                        Interlocked.Increment(ref totalConverted);

                    dispatcher.TryEnqueue(() =>
                    {
                        entry.Status = result.Success
                            ? ExtractionStatus.Done
                            : ExtractionStatus.Error;
                        ConversionProgress.Value = (double)processed / totalFiles * 100;
                    });
                }
            }, token);

            stopwatch.Stop();
            ConversionProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Converted {totalConverted}/{totalFiles} files in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Conversion cancelled");
        }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConvertButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ConvertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
    }

    // ── Video Preview ───────────────────────────────────────────────────

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilesListView.SelectedItem is SfdFileEntry entry)
            await LoadVideoPreview(entry);
        else
            ClearPreview();
    }

    private async Task LoadVideoPreview(SfdFileEntry entry)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        StopPlayback();

        PreviewPanel.Visibility = Visibility.Visible;
        PreviewColumn.Width = new GridLength(350);
        PreviewLoading.IsActive = true;
        VideoPlaceholderIcon.Visibility = Visibility.Collapsed;
        PreviewErrorText.Visibility = Visibility.Collapsed;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PlaybackSlider.Value = 0;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";

        PreviewFileNameText.Text = entry.FileName;
        var infoItems = new List<string>();
        if (!string.IsNullOrEmpty(entry.ResolutionDisplay)) infoItems.Add(entry.ResolutionDisplay);
        if (!string.IsNullOrEmpty(entry.DurationDisplay)) infoItems.Add(entry.DurationDisplay);
        if (!string.IsNullOrEmpty(entry.SizeDisplay)) infoItems.Add(entry.SizeDisplay);
        PreviewInfoText.Text = string.Join(" | ", infoItems);

        if (!_ffmpegAvailable)
        {
            PreviewLoading.IsActive = false;
            VideoPlaceholderIcon.Visibility = Visibility.Visible;
            PreviewErrorText.Text = "ffmpeg required for preview";
            PreviewErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Check cache
        string? mp4Path = null;
        if (_previewCache.TryGetValue(entry.FilePath, out var cached) && File.Exists(cached))
        {
            mp4Path = cached;
        }
        else
        {
            try
            {
                mp4Path = await Task.Run(() =>
                {
                    Directory.CreateDirectory(_tempDir);
                    var result = ConvertFile(entry.FilePath, _tempDir,
                        cancellationToken: cts.Token);
                    return result.Success ? result.OutputPath : null;
                }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (cts.Token.IsCancellationRequested) return;

                PreviewLoading.IsActive = false;
                VideoPlaceholderIcon.Visibility = Visibility.Visible;
                PreviewErrorText.Text = $"Preview error: {ex.Message}";
                PreviewErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (mp4Path != null)
                _previewCache[entry.FilePath] = mp4Path;
        }

        if (cts.Token.IsCancellationRequested) return;

        PreviewLoading.IsActive = false;

        if (mp4Path != null && File.Exists(mp4Path))
        {
            StartPlayback(mp4Path);
        }
        else
        {
            VideoPlaceholderIcon.Visibility = Visibility.Visible;
            PreviewErrorText.Text = "Failed to convert for preview";
            PreviewErrorText.Visibility = Visibility.Visible;
        }
    }

    private void StartPlayback(string mp4Path)
    {
        _mediaPlayer?.Dispose();
        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(mp4Path));
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

        VideoPlayer.SetMediaPlayer(_mediaPlayer);
        VideoPlaceholderIcon.Visibility = Visibility.Collapsed;

        _positionTimer?.Stop();
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += PositionTimer_Tick;

        _mediaPlayer.Play();
        _positionTimer.Start();
        PlayPauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        UpdatePlayPauseIcon(true);
    }

    private void StopPlayback()
    {
        _positionTimer?.Stop();
        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
        }

        VideoPlayer.SetMediaPlayer(null);

        if (_mediaPlayer != null)
        {
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        UpdatePlayPauseIcon(false);
        _updatingSlider = true;
        PlaybackSlider.Value = 0;
        _updatingSlider = false;
        CurrentTimeText.Text = "0:00";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;

        var session = _mediaPlayer.PlaybackSession;
        if (session.PlaybackState == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
        }
        else
        {
            _mediaPlayer.Play();
            _positionTimer?.Start();
            UpdatePlayPauseIcon(true);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Pause();
        _mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
        _positionTimer?.Stop();
        UpdatePlayPauseIcon(false);
        _updatingSlider = true;
        PlaybackSlider.Value = 0;
        _updatingSlider = false;
        CurrentTimeText.Text = "0:00";
    }

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
            _updatingSlider = true;
            PlaybackSlider.Value = 100;
            _updatingSlider = false;
        });
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
            PreviewErrorText.Text = $"Playback error: {args.ErrorMessage}";
            PreviewErrorText.Visibility = Visibility.Visible;
        });
    }

    private void PositionTimer_Tick(object? sender, object e)
    {
        if (_mediaPlayer == null) return;

        var session = _mediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (duration.TotalSeconds > 0)
        {
            _updatingSlider = true;
            PlaybackSlider.Value = session.Position.TotalSeconds / duration.TotalSeconds * 100;
            _updatingSlider = false;
            CurrentTimeText.Text = FormatTime(session.Position);
            TotalTimeText.Text = FormatTime(duration);
        }
    }

    private void PlaybackSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider || _mediaPlayer == null) return;

        var duration = _mediaPlayer.PlaybackSession.NaturalDuration;
        if (duration.TotalSeconds > 0)
        {
            var newPosition = TimeSpan.FromSeconds(e.NewValue / 100.0 * duration.TotalSeconds);
            _mediaPlayer.PlaybackSession.Position = newPosition;
        }
    }

    private void UpdatePlayPauseIcon(bool isPlaying)
    {
        PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
    }

    private void ClearPreview()
    {
        StopPlayback();
        _previewCts?.Cancel();
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(0);
        PreviewLoading.IsActive = false;
        VideoPlaceholderIcon.Visibility = Visibility.Visible;
        PreviewFileNameText.Text = "";
        PreviewInfoText.Text = "";
        PreviewErrorText.Visibility = Visibility.Collapsed;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";
        PlaybackSlider.Value = 0;
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalMinutes >= 60
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024 => $"{bytes / 1_024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }

    // ── Format dispatch ──────────────────────────────────────────────────

    private static bool IsStrFormat(string path)
    {
        return Path.GetExtension(path).Equals(".str", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrVideoFile(string path)
    {
        try
        {
            var header = new byte[16];
            using var fs = File.OpenRead(path);
            if (fs.Read(header, 0, 16) < 16) return false;
            // Reject AFS archives (DC SPEECH.STR)
            return !(header[0] == 'A' && header[1] == 'F' && header[2] == 'S' && header[3] == 0);
        }
        catch
        {
            return false;
        }
    }

    private static (string duration, string resolution) ProbeFile(string path)
    {
        if (IsStrFormat(path))
        {
            var probe = StrConverter.Probe(path);
            return (probe?.DurationDisplay ?? "", probe?.ResolutionDisplay ?? "");
        }
        else
        {
            var probe = SfdConverter.Probe(path);
            return (probe?.DurationDisplay ?? "", probe?.ResolutionDisplay ?? "");
        }
    }

    private static SfdConvertResult ConvertFile(string path, string outputDir,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        return IsStrFormat(path)
            ? StrConverter.ConvertToMp4(path, outputDir, progress, cancellationToken)
            : SfdConverter.ConvertToMp4(path, outputDir, progress, cancellationToken);
    }
}
