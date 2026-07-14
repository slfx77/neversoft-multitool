using System.Security.Cryptography;
using System.Text;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool;

internal sealed class VideoConverterTabPreviewController : IDisposable
{
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

    // Temp files written for archive-sourced previews; cleaned up on tab unload.
    private readonly List<string> _archivePreviewTempFiles = [];
    private readonly string _cacheDir;
    private readonly VideoPreviewView _view;
    private MediaPlayer? _mediaPlayer;
    private DispatcherTimer? _positionTimer;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private bool _updatingSlider;

    public VideoConverterTabPreviewController(VideoPreviewView view)
    {
        _view = view;
        _cacheDir = Path.Combine(view.TempDir, "cache");
        _ = Task.Run(PruneStaleCacheEntries);
    }

    /// <summary>Invoked on the UI thread when a new source finishes opening.</summary>
    public Action? MediaOpened { get; set; }

    /// <summary>Natural pixel size of the currently opened stream, if any.</summary>
    public (uint Width, uint Height)? NaturalVideoSize
    {
        get
        {
            var session = _mediaPlayer?.PlaybackSession;
            if (session == null || session.NaturalVideoWidth == 0)
                return null;
            return (session.NaturalVideoWidth, session.NaturalVideoHeight);
        }
    }

    public void Dispose()
    {
        StopPlayback();
        _previewCts?.Dispose();
        _previewCts = null;
        _previewTask = null;

        foreach (var tempFile in _archivePreviewTempFiles)
        {
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch
            {
                /* ignore */
            }
        }

        _archivePreviewTempFiles.Clear();
    }

    /// <summary>
    ///     Archive-sourced video preview needs a real path for MediaPlayerElement
    ///     and for the STR/VID decoders. Write bytes to a uniquely-named temp file
    ///     and track it for cleanup on tab unload.
    /// </summary>
    private string EnsurePreviewPath(SfdFileEntry entry)
    {
        if (entry.Source.FileSystemPath is { } filesystemPath)
            return filesystemPath;

        var tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "VideoPreview");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}_{entry.FileName}");
        File.WriteAllBytes(tempPath, entry.Source.ReadBytes());
        _archivePreviewTempFiles.Add(tempPath);
        return tempPath;
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

        string previewPath;
        try
        {
            previewPath = EnsurePreviewPath(entry);
        }
        catch (Exception ex)
        {
            ShowPreviewError($"Preview prep failed: {ex.Message}");
            return;
        }

        if (OrdinalIsStr(entry.FileName)
            && await TryStartDirectStrPlaybackAsync(previewPath, cts.Token))
        {
            return;
        }

        if (OrdinalIsVid(entry.FileName)
            && await TryStartDirectVidPlaybackAsync(previewPath, cts.Token))
        {
            return;
        }

        if (!ffmpegAvailable)
        {
            ShowPreviewError("ffmpeg required for preview");
            return;
        }

        // Converted previews cache on disk keyed by source identity, so a file
        // previewed in an earlier session plays instantly.
        var cachePath = GetPreviewCachePath(entry);
        string? mp4Path = null;
        if (File.Exists(cachePath))
        {
            mp4Path = cachePath;
        }
        else
        {
            var conversionTask = Task.Run(() =>
            {
                // Convert into a unique scratch dir (the converter names output
                // by stem), then move the result to its cache slot.
                var scratchDir = Path.Combine(_view.TempDir, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratchDir);
                try
                {
                    var result = VideoConverterTabOperations.ConvertFile(
                        previewPath,
                        scratchDir,
                        cancellationToken: cts.Token,
                        previewQuality: true);
                    if (!result.Success || result.OutputPath == null)
                        return null;

                    Directory.CreateDirectory(_cacheDir);
                    File.Move(result.OutputPath, cachePath, true);
                    return cachePath;
                }
                finally
                {
                    try
                    {
                        Directory.Delete(scratchDir, true);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
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

        var position = TimeSpan.FromSeconds(sliderValue / 100.0 * duration.TotalSeconds);
        _mediaPlayer.PlaybackSession.Position = position;
        // Immediate feedback while scrubbing — the position timer only ticks
        // every 200 ms and stalls while a seek is still decoding.
        _view.CurrentTimeText.Text = VideoConverterTabOperations.FormatTime(position);
    }

    public void ClearPreview()
    {
        StopPlayback();
        _previewCts?.Cancel();
        _previewTask = null;

        _view.PreviewLoading.IsActive = false;
        _view.VideoPlaceholderPanel.Visibility = Visibility.Visible;
        _view.PreviewFileNameText.Text = string.Empty;
        _view.PreviewInfoText.Text = string.Empty;
        _view.PreviewErrorText.Visibility = Visibility.Collapsed;
        ResetPlaybackPosition();
    }

    /// <summary>
    ///     Cache key = source identity: path + length + mtime for filesystem
    ///     files (re-converts when the file changes), display path + size for
    ///     archive entries (their preview temp paths are per-session GUIDs).
    /// </summary>
    private string GetPreviewCachePath(SfdFileEntry entry)
    {
        string identity;
        if (entry.Source.FileSystemPath is { } filePath && File.Exists(filePath))
        {
            var info = new FileInfo(filePath);
            identity = $"{filePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        else
        {
            identity = $"{entry.FilePath}|{entry.SizeDisplay}";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_cacheDir, $"{hash}.mp4");
    }

    private void PruneStaleCacheEntries()
    {
        try
        {
            if (!Directory.Exists(_cacheDir))
                return;

            var cutoff = DateTime.UtcNow - CacheMaxAge;
            foreach (var file in Directory.EnumerateFiles(_cacheDir, "*.mp4"))
            {
                try
                {
                    if (File.GetLastAccessTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private static bool OrdinalIsStr(string fileName)
        => fileName.EndsWith(".str", StringComparison.OrdinalIgnoreCase);

    private static bool OrdinalIsVid(string fileName)
        => fileName.EndsWith(".vid", StringComparison.OrdinalIgnoreCase);

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

    private async Task<bool> TryStartDirectVidPlaybackAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var mediaSource = await Task.Run(() => Vid1MediaSource.Create(filePath), cancellationToken);
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
        _view.PreviewLoading.IsActive = true;
        _view.VideoPlaceholderPanel.Visibility = Visibility.Collapsed;
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
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;

        _view.VideoPlayer.SetMediaPlayer(_mediaPlayer);
        _view.VideoPlaceholderPanel.Visibility = Visibility.Collapsed;

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
            _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
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

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        _view.VideoPlayer.DispatcherQueue.TryEnqueue(() => MediaOpened?.Invoke());
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
        _view.VideoPlaceholderPanel.Visibility = Visibility.Visible;
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
}
