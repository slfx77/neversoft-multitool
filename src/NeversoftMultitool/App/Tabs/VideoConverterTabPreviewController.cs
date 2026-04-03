using CommunityToolkit.WinUI.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool;

internal sealed class VideoConverterTabPreviewController : IDisposable
{
    private readonly Dictionary<string, string> _previewCache = [];
    private readonly VideoPreviewView _view;
    private CancellationTokenSource? _previewCts;
    private MediaPlayer? _mediaPlayer;
    private DispatcherTimer? _positionTimer;
    private Task? _previewTask;
    private bool _updatingSlider;

    public VideoConverterTabPreviewController(VideoPreviewView view)
    {
        _view = view;
    }

    public async Task ShowPreviewAsync(SfdFileEntry entry, bool ffmpegAvailable)
    {
        var previousCts = _previewCts;
        if (previousCts != null)
        {
            _previewCts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        if (_previewTask is { IsCompleted: false } previousTask)
        {
            try
            {
                await previousTask;
            }
            catch
            {
                // Expected when switching selection while a preview conversion is in flight.
            }
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;

        StopPlayback();
        ShowPreviewShell(entry);

        if (VideoConverterTabOperations.IsStrFormat(entry.FilePath)
            && await TryStartDirectStrPlaybackAsync(entry.FilePath, cts.Token))
        {
            return;
        }

        if (!ffmpegAvailable)
        {
            ShowPreviewError("ffmpeg required for preview");
            return;
        }

        string? mp4Path = null;
        if (_previewCache.TryGetValue(entry.FilePath, out var cachedPath) && File.Exists(cachedPath))
        {
            mp4Path = cachedPath;
        }
        else
        {
            var conversionTask = Task.Run(() =>
            {
                Directory.CreateDirectory(_view.TempDir);
                var result = VideoConverterTabOperations.ConvertFile(
                    entry.FilePath,
                    _view.TempDir,
                    cancellationToken: cts.Token);
                return result.Success ? result.OutputPath : null;
            }, cts.Token);
            _previewTask = conversionTask;

            try
            {
                mp4Path = await conversionTask;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (cts.Token.IsCancellationRequested)
                    return;

                ShowPreviewError($"Preview error: {ex.Message}");
                return;
            }

            if (mp4Path != null)
                _previewCache[entry.FilePath] = mp4Path;
        }

        if (cts.Token.IsCancellationRequested)
            return;

        _view.PreviewLoading.IsActive = false;
        if (mp4Path != null && File.Exists(mp4Path))
        {
            StartPlayback(MediaSource.CreateFromUri(new Uri(mp4Path)));
            return;
        }

        ShowPreviewError("Failed to convert for preview");
    }

    public void TogglePlayPause()
    {
        if (_mediaPlayer == null)
            return;

        var session = _mediaPlayer.PlaybackSession;
        if (session.PlaybackState == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
            return;
        }

        _mediaPlayer.Play();
        _positionTimer?.Start();
        UpdatePlayPauseIcon(true);
    }

    public void Stop()
    {
        if (_mediaPlayer == null)
            return;

        _mediaPlayer.Pause();
        _mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
        _positionTimer?.Stop();
        UpdatePlayPauseIcon(false);
        ResetPlaybackPosition();
    }

    public void Seek(double sliderValue)
    {
        if (_updatingSlider || _mediaPlayer == null)
            return;

        var duration = _mediaPlayer.PlaybackSession.NaturalDuration;
        if (duration.TotalSeconds <= 0)
            return;

        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(sliderValue / 100.0 * duration.TotalSeconds);
    }

    public void ClearPreview()
    {
        StopPlayback();
        _previewCts?.Cancel();
        _previewTask = null;

        _view.PreviewPanel.Visibility = Visibility.Collapsed;
        _view.PreviewSplitter.Visibility = Visibility.Collapsed;
        _view.SplitterColumn.Width = new GridLength(0);
        _view.PreviewColumn.Width = new GridLength(0);
        _view.PreviewLoading.IsActive = false;
        _view.VideoPlaceholderIcon.Visibility = Visibility.Visible;
        _view.PreviewFileNameText.Text = string.Empty;
        _view.PreviewInfoText.Text = string.Empty;
        _view.PreviewErrorText.Visibility = Visibility.Collapsed;
        ResetPlaybackPosition();
    }

    private async Task<bool> TryStartDirectStrPlaybackAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var strData = await Task.Run(() => File.ReadAllBytes(filePath), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return false;

            var mediaSource = await Task.Run(() => StrMediaSource.Create(strData), cancellationToken);
            if (cancellationToken.IsCancellationRequested || mediaSource == null)
                return false;

            _view.PreviewLoading.IsActive = false;
            StartPlayback(mediaSource);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void ShowPreviewShell(SfdFileEntry entry)
    {
        _view.PreviewPanel.Visibility = Visibility.Visible;
        _view.PreviewSplitter.Visibility = Visibility.Visible;
        _view.SplitterColumn.Width = new GridLength(8);
        if (_view.PreviewColumn.Width.Value <= 0)
            _view.PreviewColumn.Width = new GridLength(350);

        _view.PreviewLoading.IsActive = true;
        _view.VideoPlaceholderIcon.Visibility = Visibility.Collapsed;
        _view.PreviewErrorText.Visibility = Visibility.Collapsed;
        _view.PlayPauseButton.IsEnabled = false;
        _view.StopButton.IsEnabled = false;
        ResetPlaybackPosition();

        _view.PreviewFileNameText.Text = entry.FileName;
        var infoItems = new List<string>();
        if (!string.IsNullOrEmpty(entry.ResolutionDisplay))
            infoItems.Add(entry.ResolutionDisplay);
        if (!string.IsNullOrEmpty(entry.DurationDisplay))
            infoItems.Add(entry.DurationDisplay);
        if (!string.IsNullOrEmpty(entry.SizeDisplay))
            infoItems.Add(entry.SizeDisplay);
        _view.PreviewInfoText.Text = string.Join(" | ", infoItems);
    }

    private void StartPlayback(MediaSource source)
    {
        StopPlayback();

        _mediaPlayer = new MediaPlayer
        {
            Source = source
        };
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

        _view.VideoPlayer.SetMediaPlayer(_mediaPlayer);
        _view.VideoPlaceholderIcon.Visibility = Visibility.Collapsed;

        _positionTimer?.Stop();
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _positionTimer.Tick += PositionTimer_Tick;

        _mediaPlayer.Play();
        _positionTimer.Start();
        _view.PlayPauseButton.IsEnabled = true;
        _view.StopButton.IsEnabled = true;
        UpdatePlayPauseIcon(true);
    }

    private void StopPlayback()
    {
        if (_positionTimer != null)
        {
            _positionTimer.Stop();
            _positionTimer.Tick -= PositionTimer_Tick;
            _positionTimer = null;
        }

        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
        }

        _view.VideoPlayer.SetMediaPlayer(null);

        if (_mediaPlayer != null)
        {
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        UpdatePlayPauseIcon(false);
    }

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        _view.VideoPlayer.DispatcherQueue.TryEnqueue(() =>
        {
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
            _updatingSlider = true;
            _view.PlaybackSlider.Value = 100;
            _updatingSlider = false;
        });
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _view.VideoPlayer.DispatcherQueue.TryEnqueue(() =>
        {
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
            var detail = !string.IsNullOrWhiteSpace(args.ErrorMessage)
                ? args.ErrorMessage
                : args.ExtendedErrorCode.Message;
            ShowPreviewError($"Playback error: {detail}");
        });
    }

    private void PositionTimer_Tick(object? sender, object e)
    {
        if (_mediaPlayer == null)
            return;

        var session = _mediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (duration.TotalSeconds <= 0)
            return;

        _updatingSlider = true;
        _view.PlaybackSlider.Value = session.Position.TotalSeconds / duration.TotalSeconds * 100;
        _updatingSlider = false;
        _view.CurrentTimeText.Text = VideoConverterTabOperations.FormatTime(session.Position);
        _view.TotalTimeText.Text = VideoConverterTabOperations.FormatTime(duration);
    }

    private void ShowPreviewError(string message)
    {
        _view.PreviewLoading.IsActive = false;
        _view.VideoPlaceholderIcon.Visibility = Visibility.Visible;
        _view.PreviewErrorText.Text = message;
        _view.PreviewErrorText.Visibility = Visibility.Visible;
    }

    private void ResetPlaybackPosition()
    {
        _updatingSlider = true;
        _view.PlaybackSlider.Value = 0;
        _updatingSlider = false;
        _view.CurrentTimeText.Text = "0:00";
        _view.TotalTimeText.Text = "0:00";
    }

    private void UpdatePlayPauseIcon(bool isPlaying)
    {
        _view.PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
    }

    public void Dispose()
    {
        StopPlayback();
        _previewCts?.Dispose();
        _previewCts = null;
        _previewTask = null;
    }
}

internal sealed class VideoPreviewView
{
    public required Border PreviewPanel { get; init; }
    public required GridSplitter PreviewSplitter { get; init; }
    public required ColumnDefinition SplitterColumn { get; init; }
    public required ColumnDefinition PreviewColumn { get; init; }
    public required ProgressRing PreviewLoading { get; init; }
    public required FontIcon VideoPlaceholderIcon { get; init; }
    public required TextBlock PreviewFileNameText { get; init; }
    public required TextBlock PreviewInfoText { get; init; }
    public required TextBlock PreviewErrorText { get; init; }
    public required Button PlayPauseButton { get; init; }
    public required Button StopButton { get; init; }
    public required Slider PlaybackSlider { get; init; }
    public required TextBlock CurrentTimeText { get; init; }
    public required TextBlock TotalTimeText { get; init; }
    public required MediaPlayerElement VideoPlayer { get; init; }
    public required FontIcon PlayPauseIcon { get; init; }
    public required string TempDir { get; init; }
}
