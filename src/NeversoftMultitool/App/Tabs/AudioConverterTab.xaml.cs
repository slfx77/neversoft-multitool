using System.Collections.ObjectModel;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool;

public sealed partial class AudioConverterTab : UserControl, IDisposable
{
    private static readonly string[] ArchiveExtensions = [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"];

    private readonly AudioConverterTabConversionController _conversionController = new();
    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly List<AudioFileEntry> _parentFiles = [];
    private readonly Dictionary<string, string> _previewCache = [];

    // Temp file cache
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "AudioPreview");
    private string _inputDir = "";

    // Playback
    private MediaPlayer? _mediaPlayer;
    private string _outputDir = "";
    private bool _outputManuallySet;
    private DispatcherTimer? _positionTimer;
    private CancellationTokenSource? _previewCts;
    private bool _updatingSlider;

    public AudioConverterTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        Unloaded += AudioConverterTab_Unloaded;

        // Reclaim preview WAVs left behind if a previous session crashed
        // before Dispose could clean the temp root.
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* another instance may hold a lock — ignore */
        }
    }

    public void Dispose()
    {
        Unloaded -= AudioConverterTab_Unloaded;
        ClearPreview();
        _conversionController.Dispose();

        // Preview WAVs accumulate in per-conversion GUID subdirectories.
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // A file may still be locked by a just-disposed MediaPlayer.
        }
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;
        DefaultOutputToInput(path);

        ClearPreview();
        _previewCache.Clear();

        _items.Clear();
        _parentFiles.Clear();
        var audioFiles = AudioConverterTabOperations.FindAudioFiles(_inputDir);

        // Probe audio files for unsupported variants (e.g. ADX encoding type)
        var unsupported = new List<ScanSummaryDialog.UnsupportedFile>();
        var supported = new List<string>();
        foreach (var filePath in audioFiles)
        {
            var probe = FormatProbe.ProbeAudio(filePath);
            if (probe.Support == FormatProbe.FormatSupport.Unsupported)
                unsupported.Add(new ScanSummaryDialog.UnsupportedFile(Path.GetFileName(filePath)!,
                    probe.UnsupportedReason ?? "Unknown format"));
            else
                supported.Add(filePath);
        }

        if (unsupported.Count > 0)
        {
            var proceed = await ScanSummaryDialog.ShowIfNeeded(
                XamlRoot, supported.Count, unsupported);
            if (!proceed) return;
        }

        foreach (var filePath in supported.OrderBy(f => MakeRelativePath(f, _inputDir),
                     StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(filePath)!;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var entry = new AudioFileEntry
            {
                FileName = fileName,
                AudioFormat = AudioConverterTabOperations.DetectFormat(ext),
                Source = new FileSystemAssetSource(filePath),
                RelativePath = MakeRelativePath(filePath, _inputDir)
            };
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        UpdateUiState();
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;
        if (_inputDir.Length > 0)
            DefaultOutputToInput(_inputDir);

        ClearPreview();
        _previewCache.Clear();
        _items.Clear();
        _parentFiles.Clear();

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
            var entries = new List<AudioFileEntry>();
            foreach (var archiveEntry in backend.Entries)
            {
                if (!AudioConverterTabOperations.IsAudioFile(archiveEntry.Name)) continue;
                var ext = Path.GetExtension(archiveEntry.Name).ToLowerInvariant();
                entries.Add(new AudioFileEntry
                {
                    FileName = archiveEntry.Name,
                    AudioFormat = AudioConverterTabOperations.DetectFormat(ext),
                    Source = new ArchiveAssetSource(backend, archiveEntry),
                    RelativePath = $"{archiveName}::{archiveEntry.Name}"
                });
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var entry in entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    _parentFiles.Add(entry);
                    _items.Add(entry);
                }

                MainWindow.Instance?.SetStatus(entries.Count == 0
                    ? $"{archiveName}: no audio entries."
                    : $"Found {entries.Count} audio entrie(s) in {archiveName}.");

                UpdateUiState();
            });
        });
    }

    private static string MakeRelativePath(string file, string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir)) return Path.GetFileName(file);
        try
        {
            return Path.GetRelativePath(rootDir, file);
        }
        catch
        {
            return Path.GetFileName(file);
        }
    }

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

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
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _parentFiles.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasFiles && hasOutput;
    }

    private void ExpandCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AudioFileEntry parent } || !parent.IsExpandable) return;

        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0) return;

        if (parent.IsExpanded)
        {
            parent.IsExpanded = false;
            var removeIndex = parentIndex + 1;
            while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
                _items.RemoveAt(removeIndex);
        }
        else
        {
            EnsureChildren(parent);
            parent.IsExpanded = true;
            for (var i = 0; i < parent.CachedChildren!.Count; i++)
                _items.Insert(parentIndex + 1 + i, parent.CachedChildren[i]);
        }
    }

    private static void EnsureChildren(AudioFileEntry parent)
    {
        if (parent.CachedChildren != null) return;

        try
        {
            parent.CachedChildren = AudioConverterTabOperations.EnumerateChildren(
                parent.Source,
                parent.FileName,
                parent.AudioFormat);
        }
        catch
        {
            parent.CachedChildren = [];
        }
    }

    private async void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0) return;

        // Children enumerate lazily (each bank parses on demand) — build them
        // off-thread, then rebuild the flat list in one pass.
        var parents = _parentFiles.Where(p => p.IsExpandable).ToList();
        await Task.Run(() =>
        {
            foreach (var parent in parents)
                EnsureChildren(parent);
        });

        _items.Clear();
        foreach (var parent in _parentFiles)
        {
            _items.Add(parent);
            if (!parent.IsExpandable || parent.CachedChildren is not { Count: > 0 } children)
                continue;

            parent.IsExpanded = true;
            foreach (var child in children)
                _items.Add(child);
        }
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0) return;

        foreach (var parent in _parentFiles)
            parent.IsExpanded = false;

        _items.Clear();
        foreach (var parent in _parentFiles)
            _items.Add(parent);
    }

    /// <summary>
    ///     Selected VAB output rate; 0 = auto, letting the extractor derive each
    ///     sample's rate from its tone table (center note + fine tune).
    /// </summary>
    private int GetSelectedVabSampleRate()
    {
        var selected = (VabSampleRateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        return selected switch
        {
            "11025 Hz" => 11025,
            "22050 Hz" => 22050,
            "44100 Hz" => 44100,
            _ => 0
        };
    }

    /// <summary>
    ///     Reflects the selected VAB bank's dominant tone-table rate in the
    ///     "Auto" combo item so the user can see what detection resolved to.
    /// </summary>
    private async Task UpdateDetectedVabRateAsync(AudioFileEntry parent)
    {
        if (parent.AudioFormat != "VAB")
            return;

        if (parent.CachedChildren == null)
            await Task.Run(() => EnsureChildren(parent));

        var dominant = parent.CachedChildren!
            .Where(c => c.SampleRate > 0)
            .GroupBy(c => c.SampleRate)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        VabAutoRateItem.Content = dominant > 0
            ? $"Auto (detected: {dominant} Hz)"
            : "Auto (detected)";
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        await _conversionController.ConvertAsync(
            _parentFiles,
            _outputDir,
            GetSelectedVabSampleRate(),
            DispatcherQueue,
            ConvertButton,
            CancelButton,
            ConversionProgress);
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        await _conversionController.CancelAsync(ConvertButton, CancelButton);
    }

    // ── Audio Preview ────────────────────────────────────────────────────

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = FilesListView.SelectedItem;

        switch (selectedItem)
        {
            case AudioFileEntry { IsExpandable: true } parent:
                ShowParentInfo(parent);
                await UpdateDetectedVabRateAsync(parent);
                break;
            case AudioFileEntry parent:
                await LoadAudioPreview(parent);
                break;
            case AudioSampleEntry sample:
                await LoadAudioPreview(sample);
                break;
            default:
                ClearPreview();
                break;
        }
    }

    private void ShowParentInfo(AudioFileEntry parent)
    {
        StopPlayback();
        PreviewPlaceholderText.Visibility = Visibility.Collapsed;
        PreviewFileNameText.Text = parent.FileName;
        PreviewInfoText.Text = $"{parent.AudioFormat} soundbank\nSelect a sample to preview";
        PreviewLoading.IsActive = false;
        AudioIcon.Visibility = Visibility.Visible;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PlaybackSlider.Value = 0;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";
        PreviewErrorText.Visibility = Visibility.Collapsed;
    }

    private async Task LoadAudioPreview(IListEntry item)
    {
        var previousPreviewCts = _previewCts;
        if (previousPreviewCts != null)
        {
            _previewCts = null;
            await previousPreviewCts.CancelAsync();
            previousPreviewCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        // Snapshot the token before any await: tab teardown disposes _previewCts
        // (this same object) mid-flight, and the CancellationTokenSource.Token
        // GETTER throws ObjectDisposedException — the token struct stays safe.
        var token = cts.Token;

        StopPlayback();

        // Loading state
        PreviewPlaceholderText.Visibility = Visibility.Collapsed;
        PreviewLoading.IsActive = true;
        AudioIcon.Visibility = Visibility.Collapsed;
        PreviewErrorText.Visibility = Visibility.Collapsed;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PlaybackSlider.Value = 0;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";

        // The selected VAB rate is part of the cache identity — without it,
        // changing the rate would replay a stale WAV converted at the old one.
        var vabSampleRate = GetSelectedVabSampleRate();

        string cacheKey;
        string displayName;
        string infoText;

        if (item is AudioFileEntry parent)
        {
            cacheKey = $"{Path.Combine(_inputDir, parent.FileName)}@{vabSampleRate}";
            displayName = parent.FileName;
            infoText = parent.AudioFormat;
        }
        else if (item is AudioSampleEntry sample)
        {
            cacheKey = $"{Path.Combine(_inputDir, sample.ParentFileName)}#{sample.SampleIndex}@{vabSampleRate}";
            displayName = $"{sample.ParentFileName} #{sample.SampleIndex:D3}";
            infoText = sample.InfoDisplay;
        }
        else
        {
            ClearPreview();
            return;
        }

        PreviewFileNameText.Text = displayName;
        PreviewInfoText.Text = infoText;

        // Check cache
        string? wavPath = null;
        if (_previewCache.TryGetValue(cacheKey, out var cached) && File.Exists(cached))
        {
            wavPath = cached;
        }
        else
        {
            try
            {
                wavPath = await Task.Run(
                    () => AudioConverterTabOperations.ConvertForPreview(
                        item,
                        _tempDir,
                        _parentFiles,
                        vabSampleRate),
                    token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                PreviewLoading.IsActive = false;
                AudioIcon.Visibility = Visibility.Visible;
                PreviewErrorText.Text = $"Preview error: {ex.Message}";
                PreviewErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (wavPath != null)
                _previewCache[cacheKey] = wavPath;
        }

        if (token.IsCancellationRequested) return;

        PreviewLoading.IsActive = false;
        AudioIcon.Visibility = Visibility.Visible;

        if (wavPath != null && File.Exists(wavPath))
        {
            StartPlayback(wavPath);
        }
        else
        {
            PreviewErrorText.Text = "Failed to decode audio";
            PreviewErrorText.Visibility = Visibility.Visible;
        }
    }

    private void StartPlayback(string wavPath)
    {
        StopPlayback();

        _mediaPlayer = new MediaPlayer
        {
            Source = MediaSource.CreateFromUri(new Uri(wavPath))
        };
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

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
        _previewCts?.Dispose();
        _previewCts = null;
        PreviewPlaceholderText.Visibility = Visibility.Visible;
        PreviewLoading.IsActive = false;
        PreviewFileNameText.Text = "";
        PreviewInfoText.Text = "";
        PreviewErrorText.Visibility = Visibility.Collapsed;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";
        PlaybackSlider.Value = 0;
    }

    private static string FormatTime(TimeSpan ts)
    {
        return AudioConverterTabOperations.FormatTime(ts);
    }

    private void AudioConverterTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
